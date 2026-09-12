using System.Diagnostics.Metrics;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Staffs;
using AdminAreaManagement.Infrastructure.Authorization;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.Infrastructure.Messaging;

// Explicit configured selections are untrusted: each cycle resolves live access before opening tenant persistence.
public sealed class StaffProjectionRetryWorker(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<StaffProjectionRetryWorker> logger) : BackgroundService
{
    private static readonly Meter Metrics = new(StaffProjectionOutbox.DiagnosticsName);
    private static readonly Counter<long> Cycles = Metrics.CreateCounter<long>("staff_outbox.retry_cycles");

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (Organisations(configuration).Length == 0 || string.IsNullOrWhiteSpace(configuration["TenantWorker:SubjectId"])
            || string.IsNullOrWhiteSpace(configuration["TenantWorker:BearerToken"]))
            throw new InvalidOperationException("Staff outbox retry requires explicit TenantWorker organisation selections and a renewable worker credential.");
        return base.StartAsync(cancellationToken);
    }

    internal static Guid[] Organisations(IConfiguration configuration)
    {
        var values = configuration.GetSection("TenantWorker:OrganisationIds").Get<string[]>() ?? [];
        if (values.Length > 100 || values.Any(value => !Guid.TryParse(value, out var id) || id == Guid.Empty))
            throw new InvalidOperationException("Staff outbox retry requires at most 100 non-empty organisation selections.");
        return values.Select(Guid.Parse).Distinct().ToArray();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var failed = await DispatchCycleAsync(stoppingToken);
                failures = failed ? Math.Min(failures + 1, 3) : 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                failures = Math.Min(failures + 1, 3);
                logger.LogError("Staff outbox retry cycle failed; automatic retry remains scheduled.");
                Cycles.Add(1, new KeyValuePair<string, object?>("outcome", "configuration_failure"));
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 5 * (1 << failures))), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public async Task<bool> DispatchCycleAsync(CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var organisation in Organisations(configuration))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            await using var scope = scopes.CreateAsyncScope();
            try
            {
                var identity = new WorkerIdentity(configuration["TenantWorker:SubjectId"] ?? "", organisation,
                    configuration["TenantWorker:BearerToken"]);
                using var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("TenantWorkerAccess");
                if (!Uri.TryCreate(configuration["TenantAuthorization:AuthManagementUrl"], UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
                    throw new InvalidOperationException("An HTTPS AuthManagement URL is required.");
                http.BaseAddress = uri;
                var tenant = scope.ServiceProvider.GetRequiredService<TenantContextScope>();
                var operation = new TenantOperation(new CurrentTenantAccessClient(http, identity), identity, tenant, tenant);
                await operation.AuthorizeAsync(new RequiresTenantPermissionAttribute("TeamConfiguration.ManageStaffProfiles"), budget.Token);
                var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var outbox = new StaffProjectionOutbox(database, tenant,
                    scope.ServiceProvider.GetRequiredService<IEventBus>(),
                    scope.ServiceProvider.GetRequiredService<ILogger<StaffProjectionOutbox>>(), configuration);
                var transactions = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
                await transactions.ExecuteAsync(token =>
                    new DispatchStaffProjectionsCommand.Handler(outbox).Handle(new(), token), budget.Token);
                Cycles.Add(1, new KeyValuePair<string, object?>("outcome", "completed"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                failed = true;
                Cycles.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                logger.LogWarning("Staff outbox tenant retry failed or access was rejected; a fresh authorized attempt remains scheduled.");
            }
        }
        return failed;
    }

    private sealed record WorkerIdentity(string SubjectId, Guid SelectedOrganisationId, string? BearerToken)
        : IOperationIdentity, ITenantAccessCredential;
}
