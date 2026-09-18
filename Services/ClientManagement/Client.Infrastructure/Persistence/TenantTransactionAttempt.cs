using System.Data.Common;
using ClientManagement.Application.Common.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.PersistenceSecurity;

namespace ClientManagement.Infrastructure.Persistence;

internal sealed class TenantTransactionAttemptState(ITenantAttemptOrderObserver observer)
{
    private DbTransaction? transaction;
    private Guid organisationId;
    private bool initializing;
    private bool initialized;
    private Guid? attemptId;
    private Guid? transactionId;
    private int? backendProcessId;
    private long sequence;

    public void Begin(DbTransaction value, Guid organisation, Guid efTransactionId, int processId)
    {
        if (transaction is not null || organisation == Guid.Empty || efTransactionId == Guid.Empty || processId <= 0)
            throw new InvalidOperationException("A tenant database attempt is already active or invalid.");
        transaction = value;
        organisationId = organisation;
        initializing = true;
        attemptId = Guid.NewGuid(); transactionId = efTransactionId; backendProcessId = processId; sequence = 0;
        Record(TenantAttemptCommandCategory.Begin);
    }
    public void CompleteInitialization(DbTransaction value, Guid organisation)
    {
        if (!ReferenceEquals(transaction, value) || organisationId != organisation || !initializing)
            throw new InvalidOperationException("Tenant database initialization does not match the active attempt.");
        initializing = false;
        initialized = true;
        Record(TenantAttemptCommandCategory.ContextInitialized);
    }
    public bool Allows(DbCommand command) => initialized && ReferenceEquals(transaction, command.Transaction);
    public bool IsActive => initialized;
    public Guid OrganisationId => organisationId;
    public void RecordCommand(DbCommand command, CommandEventData eventData, bool write)
    {
        if (!Allows(command))
        {
            Record(TenantAttemptCommandCategory.RejectedBeforeDispatch);
            throw new InvalidOperationException("Tenant database command rejected before transaction context initialization.");
        }
        var source = eventData.CommandSource.ToString();
        Record(source.Contains("Sql", StringComparison.Ordinal) ? TenantAttemptCommandCategory.EfRawSql
            : write || StringComparer.Ordinal.Equals(source, "SaveChanges") ? TenantAttemptCommandCategory.EfWrite
            : TenantAttemptCommandCategory.EfRead);
    }
    public void Record(TenantAttemptCommandCategory category)
    {
        try
        {
            observer.Observe(new(attemptId, transactionId, backendProcessId,
                Interlocked.Increment(ref sequence), category));
        }
        catch
        {
            // Evidence is passive: an observer can never affect transaction behavior.
        }
    }
    public void Clear()
    {
        transaction = null; organisationId = Guid.Empty; initializing = false; initialized = false;
        attemptId = null; transactionId = null; backendProcessId = null; sequence = 0;
    }
}

public sealed class TenantCommitIndeterminateException(Exception innerException)
    : Exception("The tenant transaction commit outcome is indeterminate and was not replayed.", innerException);

internal sealed class TenantCommandGuard(TenantTransactionAttemptState attempt, ITenantContextAccessor tenant) : DbCommandInterceptor
{
    private void Demand(DbCommand command, CommandEventData eventData, bool write)
    {
        if (!attempt.Allows(command) || attempt.OrganisationId != tenant.Current.OrganisationId.Value)
        {
            attempt.Record(TenantAttemptCommandCategory.RejectedBeforeDispatch);
            throw new InvalidOperationException("Tenant database command rejected before transaction context initialization.");
        }
        attempt.RecordCommand(command, eventData, write);
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    { Demand(command, eventData, false); return result; }
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    { Demand(command, eventData, false); return ValueTask.FromResult(result); }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    { Demand(command, eventData, true); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Demand(command, eventData, true); return ValueTask.FromResult(result); }
    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result)
    { Demand(command, eventData, false); return result; }
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    { Demand(command, eventData, false); return ValueTask.FromResult(result); }
}

internal sealed class TenantTransactionExecutor(ApplicationDbContext database, ITenantContextAccessor tenant,
    TenantTransactionAttemptState attempt) : ITenantTransactionExecutor
{
    public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) =>
        ExecuteAsync(async token => { await work(token); return true; }, cancellationToken);
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var organisation = tenant.Current.OrganisationId.Value;
        if (organisation == Guid.Empty) throw new InvalidOperationException("Tenant context is not established.");
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(
            () => ExecuteAttemptAsync(work, organisation, cancellationToken));
    }
    public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var organisation = tenant.Current.OrganisationId.Value;
        if (organisation == Guid.Empty) throw new InvalidOperationException("Tenant context is not established.");
        return ExecuteAttemptAsync(work, organisation, cancellationToken);
    }
    private async Task<T> ExecuteAttemptAsync<T>(Func<CancellationToken, Task<T>> work, Guid organisation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IDbContextTransaction? transaction = null;
        try
        {
            await database.Database.OpenConnectionAsync(cancellationToken);
            transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var dbTransaction = transaction.GetDbTransaction();
            var connection = AssertNpgsql(database.Database.GetDbConnection());
            attempt.Begin(dbTransaction, organisation, transaction.TransactionId, connection.ProcessID);
            await InitializeAsync(database.Database.GetDbConnection(), dbTransaction, organisation, cancellationToken);
            attempt.CompleteInitialization(dbTransaction, organisation);
            var result = await work(cancellationToken);
            try { await transaction.CommitAsync(cancellationToken); }
            catch (Exception exception) { throw new TenantCommitIndeterminateException(exception); }
            attempt.Record(TenantAttemptCommandCategory.Commit);
            return result;
        }
        catch
        {
            if (transaction is not null)
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    attempt.Record(TenantAttemptCommandCategory.Rollback);
                }
                catch { }
            database.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            attempt.Clear();
            if (transaction is not null) await transaction.DisposeAsync();
            await database.Database.CloseConnectionAsync();
        }
    }
    private static NpgsqlConnection AssertNpgsql(DbConnection connection) => connection as NpgsqlConnection
        ?? throw new InvalidOperationException("Tenant transaction evidence requires PostgreSQL.");
    private static async Task InitializeAsync(DbConnection connection, DbTransaction transaction, Guid organisation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisation.ToString("D").ToLowerInvariant();
        command.Parameters.Add(parameter);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (!StringComparer.Ordinal.Equals(value as string, parameter.Value as string))
            throw new InvalidOperationException("Tenant database context initialization failed.");
    }
}
