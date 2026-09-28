using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdminAreaManagement.API.Services;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Teams.Commands.UpsertTeam;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MediatR;
using Npgsql;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Extensions.MultiTenancy.AspNetCore;
using Zeka.PersistenceSecurity;
using Zeka.PersistenceSecurity.Tests;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class FirstSqlPathEvidenceTests(PostgreSqlRlsRuntimeDatabase database)
    : IClassFixture<PostgreSqlRlsRuntimeDatabase>
{
    [Fact]
    public async Task AdminArea_http_get_and_post_prove_first_sql_order_on_restricted_runtime()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(organisation, evidence);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);

        var get = await client.GetAsync("/api/Teams?Filter=path&OrderBy=Name&PageNumber=1&PageSize=10");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, expectedAttempts: 1);

        evidence.Clear();
        var post = await client.PostAsJsonAsync("/api/Teams", new { Name = "Path evidence", Acronym = "PTH" });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfWrite, expectedAttempts: 1);
    }

    [Fact]
    public async Task AdminArea_http_retry_proves_fresh_attempt_order_rollback_and_single_commit()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        var fault = new RetryAfterFirstWriteBehavior();
        await using var app = await StartHttpApplicationAsync(organisation, evidence, fault: fault);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);
        fault.Arm();

        var response = await client.PostAsJsonAsync("/api/Teams", new { Name = "Retry path", Acronym = "RTP" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var attempts = AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfWrite,
            expectedAttempts: 2);
        Assert.NotEqual(attempts[0][0].AttemptId, attempts[1][0].AttemptId);
        Assert.NotEqual(attempts[0][0].TransactionId, attempts[1][0].TransactionId);
        Assert.Equal(TenantAttemptCommandCategory.Rollback, attempts[0][^1].Category);
        Assert.Equal(TenantAttemptCommandCategory.Commit, attempts[1][^1].Category);
        Assert.Equal(1, await CountTeamsAsync(organisation, "Retry path"));
    }

    [Fact]
    public async Task AdminArea_authorization_rejection_starts_no_database_attempt()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(organisation, evidence, allow: false);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);

        var response = await client.GetAsync("/api/Teams?Filter=path&OrderBy=Name&PageNumber=1&PageSize=10");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(evidence.Snapshot());
    }

    [Fact]
    public async Task Conflicting_claim_and_header_identity_is_rejected_before_persistence()
    {
        var headerOrganisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(headerOrganisation, evidence,
            claimedOrganisation: Guid.NewGuid());
        using var client = app.GetTestClient();
        SetTenantHeaders(client, headerOrganisation);

        var response = await client.PostAsJsonAsync("/api/Teams", new { Name = "Claim conflict", Acronym = "CLC" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(evidence.Snapshot());
        Assert.Equal(0, await CountTeamsAsync(headerOrganisation, "Claim conflict"));
    }

    [Fact]
    public async Task Conflicting_header_and_authorized_identity_is_rejected_before_persistence()
    {
        var authorizedOrganisation = Guid.NewGuid();
        var headerOrganisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(authorizedOrganisation, evidence);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, headerOrganisation);

        var response = await client.PostAsJsonAsync("/api/Teams", new { Name = "Grant conflict", Acronym = "GRC" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(evidence.Snapshot());
        Assert.Equal(0, await CountTeamsAsync(authorizedOrganisation, "Grant conflict"));
        Assert.Equal(0, await CountTeamsAsync(headerOrganisation, "Grant conflict"));
    }

    [Fact]
    public async Task Cross_tenant_route_identity_is_not_loaded_or_persisted()
    {
        var owner = Guid.NewGuid(); var selected = Guid.NewGuid();
        var id = await CreateTeamAsync(owner, "Route owner", "RTO");
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(selected, evidence);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, selected);

        await Assert.ThrowsAsync<NotFoundException>(() => client.DeleteAsync($"/api/Teams/{id}"));

        Assert.DoesNotContain(evidence.Snapshot(), item => item.Category == TenantAttemptCommandCategory.EfWrite);
        Assert.Equal(1, await CountTeamsAsync(owner, "Route owner"));
        Assert.Equal(0, await CountTeamsAsync(selected, "Route owner"));
    }

    [Fact]
    public async Task Cross_tenant_body_identity_is_not_loaded_or_persisted()
    {
        var owner = Guid.NewGuid(); var selected = Guid.NewGuid();
        var calls = new RepositoryCalls();
        var id = await CreateTeamAsync(owner, "Body owner", "BDO", calls);
        Assert.Equal(1, calls.Persists);
        Assert.Equal(1, calls.Saves);
        calls.Persists = calls.Saves = 0;
        var before = await TeamRowAsync(id);
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(selected, evidence, calls: calls);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, selected);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => client.PostAsJsonAsync("/api/Teams",
            new { Id = id, Name = "Body conflict", Acronym = "BDC" }));

        Assert.DoesNotContain(evidence.Snapshot(), item => item.Category == TenantAttemptCommandCategory.EfWrite);
        Assert.Equal(new NotFoundException(nameof(Team), id).Message, exception.Message);
        Assert.Equal(1, calls.MissingGets);
        Assert.Equal(0, calls.Persists);
        Assert.Equal(0, calls.Saves);
        var attempts = AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, expectedAttempts: 1);
        Assert.Equal(TenantAttemptCommandCategory.Rollback, attempts[0][^1].Category);
        Assert.DoesNotContain(evidence.Snapshot(), item => item.Category == TenantAttemptCommandCategory.Commit);
        Assert.Equal(before, await TeamRowAsync(id));
        Assert.Equal(1, await CountTeamsAsync(owner, "Body owner"));
        Assert.Equal(0, await CountTeamsAsync(selected, "Body conflict"));
    }

    [Fact]
    public async Task AdminArea_retry_worker_proves_first_sql_order_on_restricted_runtime()
    {
        var organisation = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        var configuration = RuntimeConfiguration(organisation);
        var publisher = new Publisher();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        services.AddSingleton<IInterceptor>(evidence);
        services.RemoveAll<IHostedService>();
        services.RemoveAll<ITenantAttemptOrderObserver>();
        services.AddSingleton<ITenantAttemptOrderObserver>(evidence);
        services.AddSingleton<IHttpClientFactory>(new WorkerHttpClientFactory());
        services.AddSingleton<IEventBus>(publisher);
        await using var provider = services.BuildServiceProvider();

        await using (var scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
                .Establish(new TenantContext(new TenantId(organisation), "seed"));
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
            var message = new StaffProjectionChangedV1(organisation, membership, 1, true,
                "Synthetic", "Worker", "worker", "Team", "T");
            await executor.ExecuteAsync(async token =>
            {
                context.Add(new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) });
                await context.SaveChangesAsync(token);
            }, default);
        }
        evidence.Clear();

        var worker = new StaffProjectionRetryWorker(provider.GetRequiredService<IServiceScopeFactory>(), configuration,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StaffProjectionRetryWorker>.Instance);
        Assert.False(await worker.DispatchCycleAsync(default));
        Assert.Single(publisher.Messages);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, expectedAttempts: 1,
            additionallyRequired: TenantAttemptCommandCategory.EfWrite);
    }

    [Fact]
    public async Task Failed_context_initialization_never_records_or_dispatches_tenant_sql()
    {
        const string positiveMarker = "issue45_initializer_capture_positive_8a6f27";
        const string rejectedMarker = "issue45_initializer_work_rejected_1d54c9";
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await database.ExecuteAdministratorAsync(
            "REVOKE EXECUTE ON FUNCTION pg_catalog.set_config(text,text,boolean) FROM PUBLIC");
        var captureStart = await BeginStatementCaptureAsync(positiveMarker);
        try
        {
            await using var provider = BuildRuntimeProvider(organisation, evidence);
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
                .Establish(new TenantContext(new TenantId(organisation), "failed-initializer"));
            var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            await Assert.ThrowsAsync<PostgresException>(() => executor.ExecuteAsync(async token =>
            {
                await context.Database.ExecuteSqlRawAsync($"SELECT 1 /* {rejectedMarker} */", token);
                return await context.Teams.AsNoTracking().CountAsync(token);
            }, default));

            var events = evidence.Snapshot();
            Assert.Equal([TenantAttemptCommandCategory.Begin, TenantAttemptCommandCategory.Rollback],
                events.Select(item => item.Category));
            Assert.DoesNotContain(events, item => item.Category is TenantAttemptCommandCategory.ContextInitialized
                or TenantAttemptCommandCategory.EfRead or TenantAttemptCommandCategory.EfWrite
                or TenantAttemptCommandCategory.EfRawSql);
        }
        finally
        {
            await EndStatementCaptureAsync();
            await database.ExecuteAdministratorAsync(
                "GRANT EXECUTE ON FUNCTION pg_catalog.set_config(text,text,boolean) TO PUBLIC");
        }

        await AssertFunctioningCaptureExcludesAsync(captureStart, positiveMarker, rejectedMarker);
    }

    [Fact]
    public async Task Command_from_a_different_scope_cannot_borrow_an_active_attempt()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var provider = BuildRuntimeProvider(organisation, evidence, singleConnection: false);
        await using var outerScope = provider.CreateAsyncScope();
        outerScope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "outer"));
        var executor = outerScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var outer = outerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await executor.ExecuteAsync(async token =>
        {
            await using var otherScope = provider.CreateAsyncScope();
            otherScope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
                .Establish(new TenantContext(new TenantId(organisation), "other"));
            var other = otherScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                other.Database.ExecuteSqlRawAsync("SELECT 1 /* issue45_mismatched_attempt */", token));
            _ = await outer.Teams.AsNoTracking().CountAsync(token);
        }, default);

        var events = evidence.Snapshot();
        Assert.Contains(events, item => item.Category == TenantAttemptCommandCategory.RejectedBeforeDispatch
            && item.AttemptId is null && item.TransactionId is null && item.BackendProcessId is null);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, expectedAttempts: 1);
    }

    [Fact]
    public async Task Rejected_first_sql_is_absent_from_ephemeral_server_capture()
    {
        const string positiveMarker = "issue45_server_capture_positive_b731a4";
        const string rejectedMarker = "issue45_server_non_dispatch_4bc65f";
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var provider = BuildRuntimeProvider(organisation, evidence);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "capture"));
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var captureStart = await BeginStatementCaptureAsync(positiveMarker);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                context.Database.ExecuteSqlRawAsync($"SELECT 1 /* {rejectedMarker} */"));
        }
        finally
        {
            await EndStatementCaptureAsync();
        }

        await AssertFunctioningCaptureExcludesAsync(captureStart, positiveMarker, rejectedMarker);
        Assert.Single(evidence.Snapshot(), item => item.Category == TenantAttemptCommandCategory.RejectedBeforeDispatch);
    }

    [Fact]
    public async Task Observer_failure_cannot_change_successful_transaction_behavior()
    {
        var organisation = Guid.NewGuid();
        await using var provider = BuildRuntimeProvider(organisation, new ThrowingObserver());
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "observer-failure"));
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var count = await executor.ExecuteAsync(token => context.Teams.AsNoTracking().CountAsync(token), default);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Deliberately_false_backend_pid_correlation_fails_conformance_evidence()
    {
        var organisation = Guid.NewGuid();
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(organisation, evidence);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);

        var response = await client.GetAsync("/api/Teams?Filter=pid&OrderBy=Name&PageNumber=1&PageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertCompleteAttempts(evidence, TenantAttemptCommandCategory.EfRead, expectedAttempts: 1);

        var falseEvents = evidence.Snapshot().Select(item => item.AttemptId.HasValue
            ? item with { BackendProcessId = int.MaxValue }
            : item).ToArray();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            TenantAttemptEvidenceAssertions.CompleteAttempts(falseEvents, evidence.BackendPids(),
                TenantAttemptCommandCategory.EfRead, expectedAttempts: 1));
        Assert.Contains("does not match independent pg_backend_pid()", exception.Message, StringComparison.Ordinal);
    }

    private async Task<WebApplication> StartHttpApplicationAsync(Guid organisation,
        TenantAttemptEvidenceCollector evidence, bool allow = true, RetryAfterFirstWriteBehavior? fault = null,
        Guid? claimedOrganisation = null, RepositoryCalls? calls = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var configuration = RuntimeConfiguration(organisation);
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddTenantContext();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<TenantRequestIdentity>();
        builder.Services.AddScoped<IOperationIdentity>(sp => sp.GetRequiredService<TenantRequestIdentity>());
        builder.Services.AddScoped<AdminAreaManagement.Infrastructure.Authorization.ITenantAccessCredential>(
            sp => sp.GetRequiredService<TenantRequestIdentity>());
        builder.Services.AddSingleton<ICurrentUserService, CurrentUserService>();
        builder.Services.AddSingleton(new TestAuthenticationState(claimedOrganisation));
        builder.Services.AddAuthentication(TestAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        AdminAreaManagement.Application.DependencyInjection.AddApplication(builder.Services);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(builder.Services, builder.Configuration);
        if (calls is not null)
        {
            var registration = builder.Services.Single(service => service.ServiceType == typeof(IRepositoryManager));
            builder.Services.Remove(registration);
            builder.Services.AddScoped<IRepositoryManager>(provider => new ObservedRepository(
                (IRepositoryManager)ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!), calls));
        }
        builder.Services.AddSingleton<IInterceptor>(evidence);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<ITenantAttemptOrderObserver>();
        builder.Services.AddSingleton<ITenantAttemptOrderObserver>(evidence);
        builder.Services.RemoveAll<ICurrentTenantAccess>();
        builder.Services.AddScoped<ICurrentTenantAccess>(_ => new Access(organisation, allow));
        if (fault is not null)
        {
            builder.Services.AddSingleton(fault);
            builder.Services.AddSingleton<IPipelineBehavior<UpsertTeamCommand, int>>(fault);
        }
        builder.Services.AddControllers().AddApplicationPart(typeof(AdminAreaManagement.API.Controllers.TeamsController).Assembly);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseMiddleware<TenantAccessMiddleware>();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private ConfigurationManager RuntimeConfiguration(Guid organisation, bool singleConnection = true)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = singleConnection
                ? database.SingleConnectionRuntimeString
                : database.RuntimeConnectionString,
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["TenantWorker:SubjectId"] = "worker",
            ["TenantWorker:BearerToken"] = "synthetic-test-token",
            ["TenantWorker:OrganisationIds:0"] = organisation.ToString(),
            ["FileServerPath"] = Path.GetTempPath()
        });
        return configuration;
    }

    private ServiceProvider BuildRuntimeProvider(Guid organisation, ITenantAttemptOrderObserver observer,
        bool singleConnection = true)
    {
        var configuration = RuntimeConfiguration(organisation, singleConnection);
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        if (observer is TenantAttemptEvidenceCollector evidence)
            services.AddSingleton<IInterceptor>(evidence);
        services.RemoveAll<IHostedService>();
        services.RemoveAll<ITenantAttemptOrderObserver>();
        services.AddSingleton(observer);
        return services.BuildServiceProvider();
    }

    private async Task<int> CreateTeamAsync(Guid organisation, string name, string acronym, RepositoryCalls? calls = null)
    {
        var evidence = new TenantAttemptEvidenceCollector();
        await using var app = await StartHttpApplicationAsync(organisation, evidence, calls: calls);
        using var client = app.GetTestClient();
        SetTenantHeaders(client, organisation);
        var response = await client.PostAsJsonAsync("/api/Teams", new { Name = name, Acronym = acronym });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<int>();
    }

    private async Task<string> TeamRowAsync(int id)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT row_to_json(t)::text FROM public.\"Teams\" t WHERE \"Id\"=@id", connection);
        command.Parameters.AddWithValue("id", id);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    // Forward every call to the real repositories; observe entry, not just SQL dispatch.
    private sealed class RepositoryCalls
    {
        public int MissingGets;
        public int Persists;
        public int Saves;
    }

    private sealed class ObservedRepository(IRepositoryManager inner, RepositoryCalls calls) : IRepositoryManager
    {
        public ITeamRepository Team { get; } = new ObservedTeams(inner.Team, calls);
        public IStaffMemberRepository StaffMember => inner.StaffMember;
        public IPartnerRepository Partner => inner.Partner;
        public IDocumentPartnerRepository DocumentPartner => inner.DocumentPartner;
        public ISchoolRepository School => inner.School;
        public ITrainingTypeRepository TrainingType => inner.TrainingType;
        public IProfessionRepository Profession => inner.Profession;
        public ITrainingsRepository Trainings => inner.Trainings;
        public ITrainingFieldRepository TrainingField => inner.TrainingField;
        public ICityRepository City => inner.City;
        public INationalityRepository Nationality => inner.Nationality;
        public void Save() { calls.Saves++; inner.Save(); }
        public Task SaveAsync() { calls.Saves++; return inner.SaveAsync(); }
    }

    private sealed class ObservedTeams(ITeamRepository inner, RepositoryCalls calls) : ITeamRepository
    {
        public Team Get(int id)
        {
            var team = inner.Get(id);
            if (team is null) calls.MissingGets++;
            return team!;
        }
        public void Persist(Team team) { calls.Persists++; inner.Persist(team); }
        public bool IsTeamUnique(string name) => inner.IsTeamUnique(name);
        public IQueryable<Team> GetTeams(string filter) => inner.GetTeams(filter);
        public void SoftDelete(Team team) => inner.SoftDelete(team);
        public Task<bool> ContainsStaffMembers(int teamId) => inner.ContainsStaffMembers(teamId);
        public Task<bool> TeamHasStaffMembers(int serviceId) => inner.TeamHasStaffMembers(serviceId);
    }

    private async Task<int> CountTeamsAsync(Guid organisation, string name)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM public.\"Teams\" WHERE \"OrganisationId\"=@organisation AND \"Name\"=@name", connection);
        command.Parameters.AddWithValue("organisation", organisation);
        command.Parameters.AddWithValue("name", name);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<DateTime> BeginStatementCaptureAsync(string positiveMarker)
    {
        var captureStart = DateTime.UtcNow;
        await database.ExecuteAdministratorAsync("""
            ALTER SYSTEM SET log_parameter_max_length = 0;
            ALTER SYSTEM SET log_parameter_max_length_on_error = 0;
            ALTER SYSTEM SET log_statement = 'all';
            SELECT pg_reload_conf();
            """);

        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await using var readiness = new NpgsqlCommand("show log_statement", connection);
            if (string.Equals(Convert.ToString(await readiness.ExecuteScalarAsync()), "all",
                    StringComparison.Ordinal))
                break;
            if (attempt == 19)
                throw new InvalidOperationException("PostgreSQL statement capture did not become active.");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        await using var positiveControl = new NpgsqlCommand($"SELECT 1 /* {positiveMarker} */", connection);
        await positiveControl.ExecuteScalarAsync();
        return captureStart;
    }

    private Task EndStatementCaptureAsync() => database.ExecuteAdministratorAsync("""
        ALTER SYSTEM SET log_statement = 'none';
        SELECT pg_reload_conf();
        """);

    private async Task AssertFunctioningCaptureExcludesAsync(DateTime captureStart, string positiveMarker,
        string rejectedMarker)
    {
        var logs = await database.GetLogsAsync(captureStart, DateTime.UtcNow.AddSeconds(1));
        var capture = string.Concat(logs.Stdout, logs.Stderr);
        Assert.Contains(positiveMarker, capture, StringComparison.Ordinal);
        Assert.DoesNotContain(rejectedMarker, capture, StringComparison.Ordinal);
    }

    private static TenantAttemptOrderEvent[][] AssertCompleteAttempts(TenantAttemptEvidenceCollector evidence,
        TenantAttemptCommandCategory required, int expectedAttempts,
        TenantAttemptCommandCategory? additionallyRequired = null) =>
        TenantAttemptEvidenceAssertions.CompleteAttempts(evidence.Snapshot(), evidence.BackendPids(),
            required, expectedAttempts, additionallyRequired);

    private static void SetTenantHeaders(HttpClient client, Guid organisation)
    {
        client.DefaultRequestHeaders.Authorization = new("Bearer", "synthetic-test-token");
        client.DefaultRequestHeaders.Add("X-Organisation-Id", organisation.ToString());
    }

    private sealed class Access(Guid organisation, bool allow) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
            CancellationToken cancellationToken) => Task.FromResult(allow
            ? new TenantAccessDecision(TenantAccessOutcome.Authorized, new(organisation, Guid.NewGuid(),
                ["TeamConfiguration.View", "TeamConfiguration.ManageTeams"], "test", DateTimeOffset.UtcNow))
            : new TenantAccessDecision(TenantAccessOutcome.Denied));
    }

    private sealed record TestAuthenticationState(Guid? ClaimedOrganisation);

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder, TestAuthenticationState state)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Issue45Test";
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "subject") };
            if (state.ClaimedOrganisation.HasValue)
                claims.Add(new Claim("org_id", state.ClaimedOrganisation.Value.ToString()));
            var identity = new ClaimsIdentity(claims, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private sealed class RetryAfterFirstWriteBehavior : IPipelineBehavior<UpsertTeamCommand, int>
    {
        private int armed;
        public void Arm() => Interlocked.Exchange(ref armed, 1);
        public async Task<int> Handle(UpsertTeamCommand request, RequestHandlerDelegate<int> next,
            CancellationToken cancellationToken)
        {
            var result = await next();
            if (Interlocked.Exchange(ref armed, 0) == 1)
                throw new NpgsqlException("Synthetic retryable post-dispatch failure.", new TimeoutException());
            return result;
        }
    }

    private sealed class WorkerHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new WorkerAuthorityHandler(), disposeHandler: true);
    }

    private sealed class WorkerAuthorityHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ContractVersion = 1,
                    SubjectId = "worker",
                    OrganisationId = Guid.Parse(request.RequestUri!.Segments.Last()),
                    OrganisationMembershipId = Guid.NewGuid(),
                    EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                    DecisionVersion = "test",
                    ObservedAtUtc = DateTimeOffset.UtcNow
                })
            });
    }

    private sealed class Publisher : IEventBus
    {
        public List<StaffProjectionChangedV1> Messages { get; } = [];
        public Task PublishAsync(Event message)
        {
            Messages.Add((StaffProjectionChangedV1)message);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : ITenantAttemptOrderObserver
    {
        public void Observe(TenantAttemptOrderEvent evidence) => throw new InvalidOperationException("observer failure");
    }
}
