using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Lifecycle;

/// <summary>
/// AuthManagement-owned durable LIFE-02 closure participant. The ordinary-write boundary, inbox
/// identity and outbound acknowledgement commit atomically under the organisation barrier lock.
/// </summary>
public sealed class AuthClosureParticipant(
    DbContextOptions<AuthDbContext> options,
    TimeProvider clock,
    IAuthClosureRecoveryCapability? recoveryCapability = null)
{
    private const string ParticipantId = "auth-management";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrganisationClosureParticipantCompletedV1> EnterAsync(
        CloseOrganisationParticipantV1 command,
        CancellationToken cancellationToken = default) => ExecuteAsync(
        command.Header, nameof(CloseOrganisationParticipantV1), command, async database =>
        {
            await AcquireBarrierLock(database, command.Header.OrganisationId, cancellationToken);
            var organisation = await database.Organisations.SingleOrDefaultAsync(
                x => x.Id == command.Header.OrganisationId, cancellationToken);
            if (organisation?.Status != OrganisationStatus.Closing)
                throw new InvalidOperationException("Auth closure participant requires a durable Closing organisation.");
            var existing = await database.AuthClosureParticipantExecutions.SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, cancellationToken);
            if (existing is not null)
                throw new InvalidOperationException("A distinct close command cannot replace the durable Auth closure fence.");

            var observedAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
            var boundaryAt = observedAt >= command.ClosingAt ? observedAt : command.ClosingAt;
            var token = "auth-close-" + Hash(Encoding.UTF8.GetBytes(
                $"{command.Header.OperationId:D}\n{command.Header.OrganisationId:D}"))[..32];
            var completed = new OrganisationClosureParticipantCompletedV1(
                ReplyHeader(command.Header, "enter-completed"), token, 1, boundaryAt);
            database.Add(AuthClosureParticipantExecution.Enter(command.Header.OperationId,
                command.Header.OrganisationId, command.Header.OperationRevision, token,
                boundaryAt, completed.ReceiptHash));
            return completed;
        }, cancellationToken);

    public async Task<OrganisationClosureFenceReleasedV1> ReleaseAsync(
        ReleaseOrganisationClosureFenceV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateCommandHeader(command.Header, nameof(ReleaseOrganisationClosureFenceV1));
        var observedAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
        await (recoveryCapability ?? new UnavailableAuthClosureRecoveryCapability())
            .ReleaseAsync(command, observedAt, cancellationToken);
        return await ExecuteAsync(
            command.Header, nameof(ReleaseOrganisationClosureFenceV1), command, async database =>
        {
            await AcquireBarrierLock(database, command.Header.OrganisationId, cancellationToken);
            var organisation = await database.Organisations.SingleOrDefaultAsync(
                x => x.Id == command.Header.OrganisationId, cancellationToken);
            if (organisation?.Status != OrganisationStatus.Closing)
                throw new InvalidOperationException(
                    "Auth closure release requires a durable Closing organisation.");
            var execution = await database.AuthClosureParticipantExecutions.SingleAsync(
                x => x.OperationId == command.Header.OperationId, cancellationToken);
            if (execution.State != AuthClosureParticipantState.Released
                || execution.ReleasedAt is null
                || command.Header.OperationRevision != execution.OperationRevision + 1)
                throw new InvalidOperationException(
                    "Auth closure release must bind exactly to the successor recovery revision.");
            if (!string.Equals(execution.FenceToken, command.FenceToken, StringComparison.Ordinal))
                throw new InvalidOperationException("Closure release token does not match the durable Auth fence.");
            var enteredOutput = await database.AuthClosureParticipantOutboxMessages.SingleAsync(x =>
                x.OperationId == command.Header.OperationId
                && x.MessageType == nameof(OrganisationClosureParticipantCompletedV1),
                cancellationToken);
            var entered = DeserializeOutput<OrganisationClosureParticipantCompletedV1>(
                enteredOutput.PayloadJson);
            if (command.Header.CausationId != entered.Header.MessageId
                || command.Header.CorrelationId != command.Header.OperationId)
                throw new InvalidOperationException(
                    "Auth closure release causation must bind to the durable enter receipt.");
            return new OrganisationClosureFenceReleasedV1(
                ReplyHeader(command.Header, "release-completed"), command.FenceToken,
                execution.ReleasedAt.Value);
        }, cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(LifecycleMessageHeaderV1 header, string messageType,
        object command, Func<AuthDbContext, Task<T>> transition, CancellationToken cancellationToken)
    {
        ValidateCommandHeader(header, messageType);
        var inputHash = Hash(JsonSerializer.SerializeToUtf8Bytes(command, Json));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await ExecuteAttemptAsync(header, messageType, inputHash,
                    transition, cancellationToken);
            }
            catch (Exception exception) when (IsRetryable(exception)) { }
        }
        throw new InvalidOperationException("Auth closure participant could not reconcile concurrent delivery.");
    }

    private static void ValidateCommandHeader(LifecycleMessageHeaderV1 header, string messageType)
    {
        if (header.ParticipantId != ParticipantId || header.ContractVersion != LifecycleContractV1.Version)
            throw new InvalidOperationException("Auth closure participant identity is required.");
        var phase = messageType switch
        {
            nameof(CloseOrganisationParticipantV1) => "enter-fence",
            nameof(ReleaseOrganisationClosureFenceV1) => "release-fence",
            _ => throw new InvalidOperationException("Unsupported Auth closure participant command.")
        };
        if (header.MessageId != LifecycleMessageIdentityV1.ForPhase(
                header.OperationId, ParticipantId, phase, header.OperationRevision))
            throw new InvalidOperationException(
                "Auth closure participant commands require the canonical deterministic message identity.");
        if (messageType == nameof(CloseOrganisationParticipantV1)
            && (header.CausationId != header.OperationId
                || header.CorrelationId != header.OperationId))
            throw new InvalidOperationException(
                "Auth closure entry must be caused by and correlated to the closure operation.");
    }

    private async Task<T> ExecuteAttemptAsync<T>(LifecycleMessageHeaderV1 header,
        string messageType, string inputHash, Func<AuthDbContext, Task<T>> transition,
        CancellationToken cancellationToken)
    {
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            await InitializeAsync(database, transaction, header.OrganisationId, cancellationToken);
            try
            {
                var prior = await database.AuthClosureParticipantInboxReceipts.SingleOrDefaultAsync(
                    x => x.MessageId == header.MessageId, cancellationToken);
                if (prior is not null)
                {
                    if (prior.OperationId != header.OperationId
                        || prior.OrganisationId != header.OrganisationId
                        || prior.MessageType != messageType
                        || prior.PayloadSha256 != inputHash)
                        throw new InvalidOperationException("Closure inbox identity conflicts with a prior command.");
                    var priorOutput = await database.AuthClosureParticipantOutboxMessages.SingleAsync(
                        x => x.OperationId == header.OperationId && x.MessageType == typeof(T).Name,
                        cancellationToken);
                    return DeserializeOutput<T>(priorOutput.PayloadJson);
                }

                database.Add(AuthClosureParticipantInboxReceipt.Create(header.MessageId,
                    header.OperationId, header.OrganisationId, messageType, inputHash, clock.GetUtcNow()));
                var output = await transition(database);
                var payloadJson = JsonSerializer.Serialize(output, Json);
                database.Add(AuthClosureParticipantOutboxMessage.Create(Guid.NewGuid(),
                    header.OperationId, header.OrganisationId, typeof(T).Name, payloadJson,
                    Hash(Encoding.UTF8.GetBytes(payloadJson)), clock.GetUtcNow()));
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return output;
            }
            finally
            {
                database.LifecycleTransaction = null;
                database.LifecycleOrganisationId = Guid.Empty;
            }
        });
    }

    private static async Task AcquireBarrierLock(AuthDbContext database, Guid organisationId,
        CancellationToken cancellationToken)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Auth closure participant transaction is required.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation_id, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure } => true,
        DbUpdateException { InnerException: not null } update => IsRetryable(update.InnerException!),
        InvalidOperationException { InnerException: not null } wrapper => IsRetryable(wrapper.InnerException!),
        _ => false
    };

    private static async Task InitializeAsync(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisationId, CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
            throw new InvalidOperationException("Auth closure participant context initialization failed.");
        database.LifecycleOrganisationId = organisationId;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }

    private static LifecycleMessageHeaderV1 ReplyHeader(LifecycleMessageHeaderV1 command,
        string responsePhase) => new(
        command.OperationId, command.OrganisationId, command.OperationRevision,
        ParticipantId, LifecycleContractV1.Version,
        LifecycleMessageIdentityV1.ForPhase(command.OperationId, ParticipantId, responsePhase,
            command.OperationRevision), command.MessageId,
        command.CorrelationId);

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static T DeserializeOutput<T>(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var header = JsonSerializer.Deserialize<LifecycleMessageHeaderV1>(
            root.GetProperty("header"), Json)
            ?? throw new InvalidOperationException("Durable closure replay header is invalid.");
        object output = typeof(T) == typeof(OrganisationClosureParticipantCompletedV1)
            ? new OrganisationClosureParticipantCompletedV1(header,
                root.GetProperty("fenceToken").GetString()!,
                root.GetProperty("fenceRevision").GetInt64(),
                root.GetProperty("boundaryEstablishedAt").GetDateTimeOffset())
            : typeof(T) == typeof(OrganisationClosureFenceReleasedV1)
                ? new OrganisationClosureFenceReleasedV1(header,
                    root.GetProperty("fenceToken").GetString()!,
                    root.GetProperty("releasedAt").GetDateTimeOffset())
                : throw new InvalidOperationException("Unsupported durable Auth closure output type.");
        return (T)output;
    }
}
