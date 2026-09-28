using System.Data;
using System.Data.Common;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using AuthManager.Core.Organisations;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

public sealed class LifecycleRegistryReader(AuthDbContext database) : ILifecycleRegistryReader
{
    public async Task<LifecycleRegistry?> ReadActiveAsync(CancellationToken cancellationToken)
    {
        // One query binds pointer and immutable bindings to one database observation.
        var revision = await (from pointer in database.Set<LifecycleRegistryActivationRecord>().AsNoTracking()
            join record in database.Set<LifecycleRegistryRevisionRecord>().Include(x => x.Bindings)
                on pointer.RevisionId equals record.Id
            where pointer.Id == 1 select record).SingleOrDefaultAsync(cancellationToken);
        return revision?.ToRegistry();
    }
}

/// <summary>Owner/migrator fixture adapter; deliberately absent from runtime registrations.</summary>
public sealed class ReviewedLifecycleRegistryActivation(AuthDbContext database) : IReviewedLifecycleRegistryActivation
{
    public async Task<bool> ActivateAsync(LifecycleRegistry registry, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var pointer = await database.Set<LifecycleRegistryActivationRecord>().SingleOrDefaultAsync(cancellationToken);
        if ((pointer?.Version ?? 0) != expectedVersion) return false;
        if (await database.Set<LifecycleRegistryRevisionRecord>().AnyAsync(x => x.Id == registry.Revision, cancellationToken))
            return false; // No replacement or reactivation of a revision under an existing identity.
        database.Add(LifecycleRegistryRevisionRecord.From(registry));
        if (pointer is null) database.Add(new LifecycleRegistryActivationRecord { RevisionId = registry.Revision, Version = 1 });
        else { pointer.RevisionId = registry.Revision; pointer.Version++; }
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception is DbUpdateConcurrencyException
            || exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { database.ChangeTracker.Clear(); return false; }
    }
}

internal sealed class LifecycleCommandGuard(AuthDbContext database) : DbCommandInterceptor
{
    private void Demand(DbCommand command, CommandEventData data)
    {
        if (!database.LifecycleOnly) return;
        if (database.LifecycleOrganisationId == Guid.Empty || database.LifecycleTransaction is null
            || !ReferenceEquals(database.LifecycleTransaction, command.Transaction)
            || data.CommandSource == CommandSource.ExecuteSqlRaw || data.CommandSource == CommandSource.FromSqlQuery)
            throw new InvalidOperationException("Lifecycle SQL requires an initialized transaction attempt.");
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result)
    { Demand(command, data); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Demand(command, data); return ValueTask.FromResult(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData data, InterceptionResult<int> result)
    { Demand(command, data); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Demand(command, data); return ValueTask.FromResult(result); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData data, InterceptionResult<object> result)
    { Demand(command, data); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Demand(command, data); return ValueTask.FromResult(result); }
}

public sealed class LifecycleAdmissionStore : ILifecycleAdmissionStore
{
    private readonly DbContextOptions<AuthDbContext> options;
    private readonly TimeProvider clock;
    private readonly IReviewedExportCategoryInventory exportInventory;

    public LifecycleAdmissionStore(DbContextOptions<AuthDbContext> options, TimeProvider clock,
        IReviewedExportCategoryInventory? exportInventory = null)
    {
        this.options = options;
        this.clock = clock;
        this.exportInventory = exportInventory ?? new ReviewedExportCategoryInventoryV1();
    }

    public async Task<AdmissionResult> AdmitAsync(AuthorizedLifecycleAdmission admission, CancellationToken cancellationToken)
    {
        // Retry the complete serializable attempt with a fresh context; uniqueness resolves concurrent replays.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    database.ChangeTracker.Clear();
                    await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
                    try
                    {
                        await InitializeAsync(database, transaction, admission.OrganisationId, cancellationToken);
                        var existing = await database.Set<LifecycleOperation>().Include(x => x.Participants)
                            .Where(x => x.IsActive || x.IdempotencyId == admission.IdempotencyId).ToListAsync(cancellationToken);
                        var replay = existing.SingleOrDefault(x => x.IdempotencyId == admission.IdempotencyId);
                        if (replay is not null)
                            return new AdmissionResult(replay.IsReplay(admission.SubjectId, admission.Family, admission.IdempotencyId)
                                ? AdmissionStatus.Replay : AdmissionStatus.Conflict, replay);
                        var conflict = existing.FirstOrDefault(x => x.IsActive && (x.Family == admission.Family
                            || admission.Family == LifecycleOperationFamily.Termination
                            || admission.Family == LifecycleOperationFamily.Export && x.Family == LifecycleOperationFamily.Termination));
                        if (conflict is not null) return new AdmissionResult(AdmissionStatus.Conflict, conflict);
                        var registry = await new LifecycleRegistryReader(database).ReadActiveAsync(cancellationToken);
                        if (registry is null) return new AdmissionResult(AdmissionStatus.RegistryUnavailable);
                        if (admission.Family == LifecycleOperationFamily.Termination)
                        {
                            var organisation = await database.Organisations.AsNoTracking().SingleOrDefaultAsync(
                                x => x.Id == admission.OrganisationId, cancellationToken);
                            if (organisation?.Status != OrganisationStatus.Active)
                                return new AdmissionResult(AdmissionStatus.Denied);
                            var reviewed = ReviewedClosureRegistryV1.Create();
                            if (registry.Revision != reviewed.Revision
                                || registry.InventoryHash != reviewed.InventoryHash)
                                return new AdmissionResult(AdmissionStatus.RegistryUnavailable);
                        }
                        var frozenExportInventory = admission.Family == LifecycleOperationFamily.Export
                            ? exportInventory.Freeze(registry) : null;
                        var operation = LifecycleOperation.Admit(Guid.NewGuid(), admission.OrganisationId, admission.SubjectId,
                            admission.Family, admission.IdempotencyId, registry, clock.GetUtcNow(), frozenExportInventory);
                        database.Add(operation);
                        await database.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new AdmissionResult(AdmissionStatus.Admitted, operation);
                    }
                    finally { database.LifecycleTransaction = null; database.LifecycleOrganisationId = Guid.Empty; }
                });
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) { }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        return new(AdmissionStatus.Unavailable);
    }

    private static async Task InitializeAsync(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisation, CancellationToken cancellationToken)
    {
        if (organisation == Guid.Empty) throw new InvalidOperationException("Authorized organisation is required.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "organisation_id";
        parameter.Value = organisation.ToString("D"); command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
            throw new InvalidOperationException("Lifecycle context initialization failed.");
        database.LifecycleOrganisationId = organisation;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }
}
