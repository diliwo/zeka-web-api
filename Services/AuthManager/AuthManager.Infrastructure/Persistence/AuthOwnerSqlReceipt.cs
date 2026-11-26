using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zeka.OwnerSqlEvidence;

namespace AuthManager.Infrastructure.Persistence;

// Per-context observation is opt-in and preserves the existing EF command pipeline.
internal sealed class AuthOwnerSqlReceipt : DbCommandInterceptor
{
    private Capture? capture;

    internal static AuthOwnerSqlReceipt? CreateIfEnabled() => OwnerSqlExecution.IsEnabled ? new() : null;

    internal static Task ObserveAuthLoadAsync(AuthDbContext database, Func<Task> load,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (database.OwnerSqlReceipt is not { } receipt) return load();
        return receipt.ObserveAsync(database, CommandSource.FromSqlQuery, async () =>
        {
            await load();
            return true;
        }, file, line);
    }

    internal static Task<T> ObserveAuthQueryAsync<T>(AuthDbContext database, Func<Task<T>> query,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        database.OwnerSqlReceipt is { } receipt
            ? receipt.ObserveAsync(database, CommandSource.LinqQuery, query, file, line)
            : query();

    internal static Task<int> ObserveAuthUpdateAsync(AuthDbContext database, Func<Task<int>> update,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        database.OwnerSqlReceipt is { } receipt
            ? receipt.ObserveAsync(database, CommandSource.ExecuteUpdate, update, file, line)
            : update();

    private async Task<T> ObserveAsync<T>(AuthDbContext database, CommandSource source,
        Func<Task<T>> operation, string file, int line)
    {
        if (capture is not null)
            throw new InvalidOperationException("OWNER_SQL_EF_NESTED_CAPTURE_REJECTED");
        var pending = new Capture(database, source, file, line);
        capture = pending;
        try
        {
            var result = await operation();
            pending.Complete();
            return result;
        }
        catch (Exception error) when (pending.TryMatchProviderFault(error, out var providerFault))
        {
            pending.Fail(providerFault);
            throw;
        }
        catch (OperationCanceledException error) when (pending.TryMatchCancellation(error))
        {
            pending.Cancel(error);
            throw;
        }
        finally
        {
            capture = null;
        }
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        capture?.Begin(command, data, result.HasResult, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
        CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
    {
        capture?.Returned(command, data, result is NpgsqlDataReader);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        capture?.Begin(command, data, result.HasResult, cancellationToken);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,
        CommandExecutedEventData data, int result, CancellationToken cancellationToken = default)
    {
        capture?.Returned(command, data, true);
        return ValueTask.FromResult(result);
    }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData data,
        CancellationToken cancellationToken = default)
    {
        capture?.ObserveFailure(command, data);
        return Task.CompletedTask;
    }

    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData data,
        CancellationToken cancellationToken = default)
    {
        capture?.ObserveCancellation(command, data);
        return Task.CompletedTask;
    }

    private sealed class Capture(AuthDbContext database, CommandSource source, string file, int line)
    {
        private Guid? commandId;
        private DbCommand? admittedCommand;
        private DbTransaction? transaction;
        private CancellationToken commandCancellation;
        private string? statement;
        private PostgresException? providerFault;
        private bool ambiguous;
        private bool cancellationObserved;
        private int terminal;
        private (Action Complete, Action<PostgresException> Fail, Action<OperationCanceledException> Cancel)? receipt;
        private bool returned;

        internal void Begin(DbCommand command, CommandEventData data, bool suppressed, CancellationToken cancellationToken)
        {
            if (commandId is not null)
            {
                ambiguous = true;
                throw new InvalidOperationException("OWNER_SQL_EF_COMMAND_REJECTED");
            }
            if (suppressed || data.CommandSource != source
                || !ReferenceEquals(data.Context, database) || command.Transaction is null
                || (source != CommandSource.ExecuteUpdate
                    && !command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)
                    && !command.CommandText.Contains("FOR SHARE", StringComparison.Ordinal)))
                throw new InvalidOperationException("OWNER_SQL_EF_COMMAND_REJECTED");
            cancellationToken.ThrowIfCancellationRequested();
            receipt = OwnerSqlExecution.ObserveAuthCommand(command, file, line);
            commandId = data.CommandId;
            admittedCommand = command;
            transaction = command.Transaction;
            commandCancellation = cancellationToken;
            statement = command.CommandText;
        }

        internal void Returned(DbCommand command, CommandExecutedEventData data, bool providerResult)
        {
            if (!Matches(command, data) || ambiguous || providerFault is not null
                || cancellationObserved || returned || !providerResult)
                throw new InvalidOperationException("OWNER_SQL_EF_RESULT_REJECTED");
            returned = true;
        }

        private bool Matches(DbCommand command, CommandEventData data) =>
            receipt is not null && commandId == data.CommandId && data.CommandSource == source
            && ReferenceEquals(data.Context, database) && ReferenceEquals(command, admittedCommand)
            && ReferenceEquals(command.Transaction, transaction)
            && string.Equals(statement, command.CommandText, StringComparison.Ordinal);

        internal void ObserveFailure(DbCommand command, CommandErrorEventData data)
        {
            if (!Matches(command, data))
                return;
            if (returned || cancellationObserved || data.Exception is not PostgresException error)
            {
                ambiguous = true;
                return;
            }

            if (providerFault is null)
                providerFault = error;
            else if (!ReferenceEquals(providerFault, error))
                ambiguous = true;
        }

        internal bool TryMatchProviderFault(Exception error, [NotNullWhen(true)] out PostgresException? fault)
        {
            fault = error as PostgresException;
            if (fault is null && error.GetType() == typeof(InvalidOperationException))
                fault = error.InnerException as PostgresException;

            return !ambiguous && !cancellationObserved && fault is not null && ReferenceEquals(providerFault, fault);
        }

        internal void ObserveCancellation(DbCommand command, CommandEndEventData data)
        {
            if (!Matches(command, data))
                return;
            if (returned || providerFault is not null)
                ambiguous = true;
            cancellationObserved = true;
        }

        // Npgsql preserves the server acknowledgement and user token on a dispatched cancellation.
        internal bool TryMatchCancellation(OperationCanceledException error) =>
            !ambiguous && !returned && cancellationObserved && commandCancellation.IsCancellationRequested
            && error.CancellationToken == commandCancellation
            && error.InnerException is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled };

        // The admitted EF operations finish consuming their single result before returning.
        internal void Complete()
        {
            commandCancellation.ThrowIfCancellationRequested();
            if (ambiguous || providerFault is not null || cancellationObserved || !returned || receipt is not { } observation
                || Interlocked.Exchange(ref terminal, 1) != 0)
                throw new InvalidOperationException("OWNER_SQL_EF_EXECUTION_UNOBSERVED");
            observation.Complete();
        }

        internal void Fail(PostgresException error)
        {
            if (receipt is { } observation && Interlocked.Exchange(ref terminal, 1) == 0)
                observation.Fail(error);
        }

        internal void Cancel(OperationCanceledException error)
        {
            if (receipt is { } observation && Interlocked.Exchange(ref terminal, 1) == 0)
                observation.Cancel(error);
        }
    }
}
