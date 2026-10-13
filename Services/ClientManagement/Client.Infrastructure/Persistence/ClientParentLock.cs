using System.Data.Common;
using ClientManagement.Application.Common.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Infrastructure.Persistence;

internal sealed class ClientParentLock(ApplicationDbContext database, ITenantContextAccessor tenant,
    TenantTransactionAttemptState attempt) : IClientParentLock
{
    public async Task<int?> ResolveCaseClientAsync(int socialCaseId, CancellationToken cancellationToken)
    {
        if (socialCaseId <= 0) return null;
        return await ScalarAsync("SELECT \"ClientId\" FROM public.\"SocialCases\" WHERE \"OrganisationId\" = @organisation AND \"SchoolRegistrationId\" = @id",
            socialCaseId, cancellationToken);
    }

    public Task<int?> ResolveActionClientAsync(int actionId, CancellationToken cancellationToken) =>
        actionId <= 0 ? Task.FromResult<int?>(null) : ScalarAsync(
            "SELECT \"ClientId\" FROM public.\"FollowUpActions\" WHERE \"OrganisationId\" = @organisation AND \"Id\" = @id",
            actionId, cancellationToken);

    public async Task LockAsync(int clientId, CancellationToken cancellationToken)
    {
        if (clientId <= 0 || await ScalarAsync(
                "SELECT \"Id\" FROM public.\"Clients\" WHERE \"OrganisationId\" = @organisation AND \"Id\" = @id AND NOT \"Softdelete\" FOR UPDATE",
                clientId, cancellationToken) != clientId)
            throw new TenantAccessException(AccessFailure.Denied);
    }

    public async Task LockWorkerAsync(int socialWorkerId, CancellationToken cancellationToken)
    {
        if (socialWorkerId <= 0 || await ScalarAsync(
                "SELECT \"SocialWorkerId\" FROM public.\"SocialWorkers\" WHERE \"OrganisationId\" = @organisation AND \"SocialWorkerId\" = @id FOR UPDATE",
                socialWorkerId, cancellationToken) != socialWorkerId)
            throw new TenantAccessException(AccessFailure.Denied);
    }

    public async Task LockActionAsync(int actionId, CancellationToken cancellationToken)
    {
        if (actionId <= 0 || await ScalarAsync(
                "SELECT \"Id\" FROM public.\"FollowUpActions\" WHERE \"OrganisationId\" = @organisation AND \"Id\" = @id FOR UPDATE",
                actionId, cancellationToken) != actionId)
            throw new TenantAccessException(AccessFailure.Denied);
    }

    private async Task<int?> ScalarAsync(string sql, int id, CancellationToken cancellationToken)
    {
        var organisation = tenant.Current.OrganisationId.Value;
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        if (!attempt.IsActive || organisation == Guid.Empty || attempt.OrganisationId != organisation || transaction is null)
            throw new InvalidOperationException("Client lock requires an initialized tenant transaction.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var tenantParameter = command.CreateParameter(); tenantParameter.ParameterName = "organisation";
        tenantParameter.Value = organisation; command.Parameters.Add(tenantParameter);
        var idParameter = command.CreateParameter(); idParameter.ParameterName = "id";
        idParameter.Value = id; command.Parameters.Add(idParameter);
        // A bounded wait fails this attempt; the caller must not replay an already observed authority decision.
        command.CommandTimeout = 3;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int result ? result : null;
    }
}
