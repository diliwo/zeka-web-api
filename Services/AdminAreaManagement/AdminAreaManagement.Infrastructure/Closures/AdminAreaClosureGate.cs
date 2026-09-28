using System.Data.Common;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.Infrastructure.Closures;

internal sealed class AdminAreaClosureGate(
    ApplicationDbContext database,
    ITenantContextAccessor tenant,
    TenantTransactionAttemptState attempt) : IAdminAreaClosureGate
{
    internal const long AdvisoryLockSeed = 4602;

    public async Task DemandOrdinaryAccessAsync(CancellationToken cancellationToken)
    {
        var organisationId = tenant.Current.OrganisationId.Value;
        await AcquireOrganisationLockAsync(database, attempt, organisationId, cancellationToken);
        if (await database.AdminAreaClosureFences.AsNoTracking()
                .AnyAsync(x => x.ReleasedAt == null, cancellationToken))
            throw new InvalidOperationException(
                "Ordinary AdminArea activity is denied after the durable organisation closure boundary.");
    }

    internal static async Task AcquireOrganisationLockAsync(ApplicationDbContext database,
        TenantTransactionAttemptState attempt, Guid organisationId, CancellationToken cancellationToken)
    {
        if (!attempt.IsActive || attempt.OrganisationId != organisationId)
            throw new InvalidOperationException("The closure advisory lock requires an initialized tenant transaction.");
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The closure advisory lock requires an active database transaction.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation, @seed))";
        command.Parameters.Add(new NpgsqlParameter("organisation", organisationId.ToString("D")));
        command.Parameters.Add(new NpgsqlParameter<long>("seed", AdvisoryLockSeed));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
