using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Infrastructure.Authorization;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Infrastructure.Messaging;

// Owns a new operation scope for every delivery; no tenant state lives on the subscriber.
public sealed class StaffProjectionConsumer(IServiceScopeFactory scopes, IConfiguration configuration)
    : IEventHandler<StaffProjectionChangedV1>
{
    public async Task Handle(StaffProjectionChangedV1 message)
    {
        if (message.OrganisationId == Guid.Empty || message.OrganisationMembershipId == Guid.Empty
            || message.Id == Guid.Empty || message.Revision <= 0)
            throw new TenantAccessException(AccessFailure.Denied);
        await using var scope = scopes.CreateAsyncScope();
        var identity = new WorkerIdentity(configuration["TenantWorker:SubjectId"] ?? string.Empty,
            message.OrganisationId, configuration["TenantWorker:BearerToken"]);
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        using var http = factory.CreateClient("TenantWorkerAccess");
        var baseAddress = configuration["TenantAuthorization:AuthManagementUrl"];
        if (!Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new TenantAccessException(AccessFailure.Unavailable);
        http.BaseAddress = uri;
        var access = new CurrentTenantAccessClient(http, identity);
        var context = scope.ServiceProvider.GetRequiredService<TenantContextScope>();
        var operation = new TenantOperation(access, identity, context, context);
        await operation.AuthorizeAsync(new RequiresTenantPermissionAttribute("TeamConfiguration.ManageStaffProfiles"),
            CancellationToken.None);
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var transactions = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var currentRevision = await transactions.ExecuteAsync(async cancellationToken =>
            await database.SocialWorkers.AsNoTracking()
                .Where(x => x.OrganisationMembershipId == message.OrganisationMembershipId)
                .Select(x => (long?)x.ProjectionVersion)
                .SingleOrDefaultAsync(cancellationToken), CancellationToken.None);
        // A previously accepted delivery is already authoritative. Avoid rejecting harmless duplicate or
        // out-of-order redelivery merely because the membership was subsequently made inactive.
        if (currentRevision is not null && message.Revision < currentRevision)
            return;
        // Membership verification is an external authorization call. Complete it before opening the
        // database transaction so an EF execution-strategy retry never replays network I/O.
        if (!await new StaffMembershipClient(http, identity).VerifyAsync(message.OrganisationId,
            message.OrganisationMembershipId, requireActive: message.Active, CancellationToken.None))
            throw new TenantAccessException(AccessFailure.Denied);
        await transactions.ExecuteAsync(async cancellationToken =>
        {
            var worker = await database.SocialWorkers.SingleOrDefaultAsync(
                x => x.OrganisationMembershipId == message.OrganisationMembershipId, cancellationToken);
            if (worker is not null && message.Revision <= worker.ProjectionVersion)
            {
                // Older/duplicate deliveries do not reactivate a revoked membership or mutate persistence.
                worker.ApplyProjection(message.OrganisationMembershipId, message.Revision, message.Id, message.Active,
                    message.FirstName, message.LastName, message.UserName, message.TeamName, message.TeamAcronym);
                return;
            }
            if (worker is null)
            {
                worker = new SocialWorker();
                database.Add(worker);
            }
            worker.ApplyProjection(message.OrganisationMembershipId, message.Revision, message.Id, message.Active,
                message.FirstName, message.LastName, message.UserName, message.TeamName, message.TeamAcronym);
            await database.SaveChangesAsync(cancellationToken);
        }, CancellationToken.None); // Unique key + revision concurrency token make competing deliveries retryable.
    }

    private sealed record WorkerIdentity(string SubjectId, Guid SelectedOrganisationId, string? BearerToken)
        : IOperationIdentity, ITenantAccessCredential;
}
