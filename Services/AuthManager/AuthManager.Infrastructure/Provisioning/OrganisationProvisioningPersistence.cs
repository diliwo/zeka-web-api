using System.Data;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthManager.Infrastructure.Provisioning;

// This adapter never retries business work or commits a caller-owned transaction.
public sealed class OrganisationProvisioningTransaction(AuthDbContext database) : IProvisioningTransaction
{
    public async Task<OrganisationProvisioningResult> ExecuteAsync(
        Func<CancellationToken, Task<OrganisationProvisioningResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (database.Database.CurrentTransaction is not null)
            throw new OrganisationProvisioningPersistenceException("PROVISIONING_CALLER_TRANSACTION_REJECTED");
        var attempted = false;
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async attemptToken =>
        {
            if (attempted)
                throw new OrganisationProvisioningPersistenceException("PROVISIONING_REPLAY_REFUSED", true);
            attempted = true;
            IDbContextTransaction? transaction = null;
            var commitStarted = false;
            var committed = false;
            try
            {
                transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, attemptToken);
                var result = await operation(attemptToken);
                await database.SaveChangesAsync(attemptToken);
                commitStarted = true;
                await transaction.CommitAsync(attemptToken);
                committed = true;
                return result;
            }
            catch (Exception error)
            {
                var rollbackUncertain = false;
                if (transaction is not null)
                {
                    using var rollbackLimit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await transaction.RollbackAsync(rollbackLimit.Token); }
                    catch { rollbackUncertain = true; }
                }
                database.ChangeTracker.Clear();
                if (commitStarted || rollbackUncertain)
                    throw new OrganisationProvisioningPersistenceException(
                        commitStarted ? "PROVISIONING_COMMIT_UNCERTAIN" : "PROVISIONING_ROLLBACK_UNCERTAIN", true);
                if (error is OperationCanceledException) throw;
                if (error is OrganisationProvisioningPersistenceException or OrganisationProvisioningException) throw;
                // Do not propagate provider messages, SQL or values across the CLI boundary.
                throw new OrganisationProvisioningPersistenceException("PROVISIONING_TRANSACTION_REJECTED");
            }
            finally
            {
                if (transaction is not null)
                {
                    try { await transaction.DisposeAsync(); }
                    catch
                    {
                        database.ChangeTracker.Clear();
                        if (committed)
                            throw new OrganisationProvisioningPersistenceException("PROVISIONING_COMMITTED_DISPOSAL_FAILED", true);
                    }
                }
            }
        }, cancellationToken);
    }
}

public sealed class OrganisationProvisioningRepository(AuthDbContext database) : IOrganisationProvisioningRepository
{
    public async Task ValidateOwnerCatalogueAsync(CancellationToken cancellationToken = default)
    {
        var owner = await database.PermissionSets.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == PermissionSet.OrganisationOwnerId, cancellationToken);
        if (owner is null || owner.Code != "Owner" || !owner.IsSystem)
            throw new OrganisationProvisioningPersistenceException("PROVISIONING_OWNER_CATALOGUE_REJECTED");
    }

    public async Task AddAsync(Organisation organisation, OrganisationMembership ownerMembership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organisation);
        ArgumentNullException.ThrowIfNull(ownerMembership);
        if (ownerMembership.PermissionSetId != PermissionSet.OrganisationOwnerId
            || ownerMembership.OrganisationId != organisation.Id || ownerMembership.UserId != organisation.OwnerUserId
            || organisation.TimeZoneId is not null || organisation.TimeZoneConfigVersion != 0)
            throw new OrganisationProvisioningPersistenceException("PROVISIONING_PERSISTENCE_INPUT_REJECTED");
        var transaction = database.Database.CurrentTransaction
            ?? throw new OrganisationProvisioningPersistenceException("PROVISIONING_TRANSACTION_REQUIRED");
        await TenantContextInitializer.InitializeAsync(database, transaction, organisation.Id, cancellationToken);
        database.AddRange(organisation, ownerMembership);
        await database.SaveChangesAsync(cancellationToken);
    }
}

public sealed class OrganisationProvisioningPersistenceException(string code, bool requiresInspection = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool RequiresInspection { get; } = requiresInspection;
}
