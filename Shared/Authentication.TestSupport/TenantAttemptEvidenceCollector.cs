using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Zeka.PersistenceSecurity;

namespace Zeka.PersistenceSecurity.Tests;

public sealed record TenantBackendPidEvidence(Guid TransactionId, int BackendProcessId);

public sealed class TenantAttemptEvidenceCollector : DbCommandInterceptor, ITenantAttemptOrderObserver
{
    private readonly ConcurrentQueue<TenantAttemptOrderEvent> events = new();
    private readonly ConcurrentQueue<TenantBackendPidEvidence> backendPids = new();

    public void Observe(TenantAttemptOrderEvent evidence) => events.Enqueue(evidence);

    public TenantAttemptOrderEvent[] Snapshot() => events.ToArray();

    public TenantBackendPidEvidence[] BackendPids() => backendPids.ToArray();

    private void Probe(DbCommand command, CommandEventData eventData)
    {
        if (command.Connection is not NpgsqlConnection connection
            || command.Transaction is not NpgsqlTransaction transaction
            || eventData.Context?.Database.CurrentTransaction is not { } efTransaction)
            return;
        using var probe = new NpgsqlCommand("select pg_backend_pid()", connection, transaction);
        backendPids.Enqueue(new(efTransaction.TransactionId, Convert.ToInt32(probe.ExecuteScalar())));
    }

    private async ValueTask ProbeAsync(DbCommand command, CommandEventData eventData,
        CancellationToken cancellationToken)
    {
        if (command.Connection is not NpgsqlConnection connection
            || command.Transaction is not NpgsqlTransaction transaction
            || eventData.Context?.Database.CurrentTransaction is not { } efTransaction)
            return;
        await using var probe = new NpgsqlCommand("select pg_backend_pid()", connection, transaction);
        backendPids.Enqueue(new(efTransaction.TransactionId,
            Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken))));
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Probe(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(command, eventData, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Probe(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await ProbeAsync(command, eventData, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Probe(command, eventData);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
        CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        await ProbeAsync(command, eventData, cancellationToken);
        return result;
    }

    public void Clear()
    {
        while (events.TryDequeue(out _)) { }
        while (backendPids.TryDequeue(out _)) { }
    }
}

public static class TenantAttemptEvidenceAssertions
{
    public static TenantAttemptOrderEvent[][] CompleteAttempts(TenantAttemptOrderEvent[] events,
        TenantBackendPidEvidence[] backendPids, TenantAttemptCommandCategory required, int expectedAttempts,
        TenantAttemptCommandCategory? additionallyRequired = null)
    {
        var attempts = events.Where(item => item.AttemptId.HasValue)
            .GroupBy(item => item.AttemptId).Select(group => group.ToArray()).ToArray();
        Require(attempts.Length == expectedAttempts,
            $"Expected {expectedAttempts} attempts but observed {attempts.Length}.");
        foreach (var attempt in attempts)
        {
            Require(attempt[0].Category == TenantAttemptCommandCategory.Begin, "Attempt does not begin with Begin.");
            Require(attempt.Length > 1 && attempt[1].Category == TenantAttemptCommandCategory.ContextInitialized,
                "Attempt does not initialize context immediately after Begin.");
            Require(attempt.Any(item => item.Category == required), $"Attempt has no {required} event.");
            if (additionallyRequired.HasValue)
                Require(attempt.Any(item => item.Category == additionallyRequired.Value),
                    $"Attempt has no {additionallyRequired.Value} event.");
            Require(attempt.Select(item => item.Sequence)
                    .SequenceEqual(Enumerable.Range(1, attempt.Length).Select(value => (long)value)),
                "Attempt event sequence is not strictly increasing and contiguous.");

            var identity = attempt[0];
            Require(identity.AttemptId.HasValue && identity.AttemptId != Guid.Empty, "Attempt ID is absent.");
            Require(identity.TransactionId.HasValue && identity.TransactionId != Guid.Empty,
                "Transaction ID is absent.");
            Require(identity.BackendProcessId > 0, "Observer backend PID is absent.");
            Require(attempt.All(item => item.AttemptId == identity.AttemptId
                    && item.TransactionId == identity.TransactionId
                    && item.BackendProcessId == identity.BackendProcessId),
                "Attempt event identities are inconsistent.");

            var independent = backendPids.Where(item => item.TransactionId == identity.TransactionId).ToArray();
            Require(independent.Length > 0, "No independent pg_backend_pid() evidence exists for the attempt.");
            Require(independent.All(item => item.BackendProcessId == identity.BackendProcessId),
                "Observer backend PID does not match independent pg_backend_pid() evidence.");
        }
        return attempts;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
