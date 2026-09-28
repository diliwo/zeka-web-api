using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

public sealed class LifecycleClosureStore(
    DbContextOptions<AuthDbContext> options,
    TimeProvider clock) : ILifecycleClosureStore
{
    private const string CoordinatorParticipant = "auth-management";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ClosureProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken) => ExecuteAsync(operationId, organisationId, async database =>
    {
        var operation = await Operation(database, operationId, cancellationToken);
        var organisation = await database.Organisations.SingleOrDefaultAsync(
            x => x.Id == organisationId, cancellationToken);
        if (operation is null || organisation is null
            || operation.Family != LifecycleOperationFamily.Termination)
            return Result(ClosureProgressStatus.Rejected, operationId, operation?.Revision ?? 0);
        if (operation.State != LifecycleOperationState.Pending)
            return Result(ClosureProgressStatus.Replay, operation.Id, operation.Revision, operation.FailureCode);

        var closingAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
        if (!organisation.BeginClosure(closingAt))
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
        operation.BeginTermination(closingAt);
        EnqueueEnteringFence(database, operation);
        await database.SaveChangesAsync(cancellationToken);
        return Result(ClosureProgressStatus.Progressed, operation.Id, operation.Revision);
    }, cancellationToken);

    public Task<ClosureProgressResult> AcceptCompletedAsync(
        OrganisationClosureParticipantCompletedV1 completed,
        CancellationToken cancellationToken) => ExecuteAsync(completed.Header.OperationId,
        completed.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, completed.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ClosureProgressStatus.Rejected, completed.Header.OperationId, 0);
        var dedupe = await Replay(database, completed.Header,
            nameof(OrganisationClosureParticipantCompletedV1), completed, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision, operation.FailureCode);
        if (operation.State != LifecycleOperationState.EnteringFence
            || completed.Header.OperationRevision != operation.Revision
            || !Matches(operation, completed.Header)
            || completed.Header.CausationId != LifecycleMessageIdentityV1.ForPhase(
                operation.Id, completed.Header.ParticipantId, "enter-fence", operation.Revision)
            || completed.BoundaryEstablishedAt < operation.ClosingAt)
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
        RecordInbox(database, completed.Header,
            nameof(OrganisationClosureParticipantCompletedV1), completed);

        operation.RecordClosureAccepted(completed.Header.ParticipantId);
        database.Add(LifecycleClosureFenceReceipt.Create(operation.Id, operation.OrganisationId,
            completed.Header.ParticipantId, completed.Header.ContractVersion,
            completed.Header.OperationRevision, completed.FenceToken, completed.FenceRevision,
            completed.BoundaryEstablishedAt, completed.ReceiptHash, completed.Header.MessageId,
            completed.Header.CausationId, completed.Header.CorrelationId));

        if (ClosureParticipants(operation).All(x => !x.Mandatory
                || x.State == LifecycleParticipantState.Accepted))
        {
            var requirements = ClosureParticipants(operation).Where(x => x.Mandatory)
                .Select(x => new ClosureFenceParticipantRequirementV1(x.ParticipantId, x.ContractVersion));
            var persisted = await database.LifecycleClosureFenceReceipts
                .Where(x => x.OperationId == operation.Id).OrderBy(x => x.ParticipantId)
                .ToListAsync(cancellationToken);
            var receipts = persisted.Select(ToContract)
                .Append(new OrganisationClosureFenceReceiptV1(completed)).ToArray();
            var evidence = new CompleteClosureFenceEvidenceV1(operation.RegistryRevision,
                operation.InventoryHash, requirements, receipts);
            var archivedAt = Later(LifecycleContractTimeV1.Normalize(clock.GetUtcNow()),
                evidence.LastBoundaryEstablishedAt);
            var organisation = await database.Organisations.SingleAsync(
                x => x.Id == operation.OrganisationId, cancellationToken);
            if (!organisation.Archive(archivedAt))
                return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
            operation.CompleteTermination(archivedAt, evidence.EvidenceHash);
            var factHeader = FactHeader(operation,
                LifecycleMessageIdentityV1.ForPhase(operation.Id, CoordinatorParticipant,
                    "archived-fact", operation.Revision),
                completed.Header.MessageId);
            Enqueue(database, operation, nameof(OrganisationArchivedV1),
                new OrganisationArchivedV1(factHeader, archivedAt, evidence.EvidenceHash));
        }

        await database.SaveChangesAsync(cancellationToken);
        return Result(operation.State == LifecycleOperationState.Completed
                ? ClosureProgressStatus.Archived : ClosureProgressStatus.AwaitingParticipants,
            operation.Id, operation.Revision, operation.FailureCode);
    }, cancellationToken);

    public Task<ClosureProgressResult> AcceptFailedAsync(OrganisationClosureParticipantFailedV1 failed,
        CancellationToken cancellationToken) => ExecuteAsync(failed.Header.OperationId,
        failed.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, failed.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ClosureProgressStatus.Rejected, failed.Header.OperationId, 0);
        var dedupe = await Replay(database, failed.Header,
            nameof(OrganisationClosureParticipantFailedV1), failed, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision, operation.FailureCode);
        if (failed.Phase != OrganisationClosureParticipantPhaseV1.EnterFence
            || operation.State != LifecycleOperationState.EnteringFence
            || failed.Header.OperationRevision != operation.Revision
            || !Matches(operation, failed.Header)
            || failed.Header.CausationId != LifecycleMessageIdentityV1.ForPhase(
                operation.Id, failed.Header.ParticipantId, "enter-fence", operation.Revision))
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
        RecordInbox(database, failed.Header,
            nameof(OrganisationClosureParticipantFailedV1), failed);
        operation.RecordClosureFailure(failed.Header.ParticipantId, failed.FailureCode,
            failed.Retryable, failed.FailedAt,
            (LifecycleClosureBoundaryDisposition)failed.BoundaryDisposition);
        await database.SaveChangesAsync(cancellationToken);
        return Result(ClosureProgressStatus.AwaitingParticipants, operation.Id,
            operation.Revision, failed.FailureCode);
    }, cancellationToken);

    public Task<ClosureProgressResult> BeginRecoveryAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken) => ExecuteAsync(operationId, organisationId, async database =>
    {
        var operation = await Operation(database, operationId, cancellationToken);
        if (operation is null || operation.Family != LifecycleOperationFamily.Termination)
            return Result(ClosureProgressStatus.Rejected, operationId, operation?.Revision ?? 0);
        if (operation.State == LifecycleOperationState.Completed)
            return Result(ClosureProgressStatus.Rejected, operation.Id, operation.Revision);
        if (operation.State == LifecycleOperationState.ReleasingFence)
            return Result(ClosureProgressStatus.Replay, operation.Id, operation.Revision);
        if (operation.State != LifecycleOperationState.EnteringFence)
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);

        var participants = ClosureParticipants(operation);
        if (participants.Any(x => x.State is LifecycleParticipantState.Pending
                or LifecycleParticipantState.Requested))
            return Result(ClosureProgressStatus.AwaitingParticipants, operation.Id,
                operation.Revision, operation.FailureCode);
        if (participants.Any(x => x.State == LifecycleParticipantState.Failed
                && (x.FailedAt is null || x.FailureRetryable is null
                    || string.IsNullOrWhiteSpace(x.FailureCode)
                    || x.FailureBoundaryDisposition != LifecycleClosureBoundaryDisposition.NotEstablished)))
            return Result(ClosureProgressStatus.Conflict, operation.Id,
                operation.Revision, "AmbiguousParticipantFailure");

        operation.BeginClosureRecovery();
        foreach (var participant in ClosureParticipants(operation)
                     .Where(x => x.State == LifecycleParticipantState.ReleaseRequested))
        {
            var receipt = await database.LifecycleClosureFenceReceipts.SingleAsync(x =>
                x.OperationId == operation.Id && x.ParticipantId == participant.ParticipantId,
                cancellationToken);
            Enqueue(database, operation, nameof(ReleaseOrganisationClosureFenceV1),
                new ReleaseOrganisationClosureFenceV1(
                    Header(operation, participant,
                        LifecycleMessageIdentityV1.ForPhase(operation.Id, participant.ParticipantId,
                            "release-fence", operation.Revision), receipt.MessageId),
                    receipt.FenceToken));
        }
        if (operation.CanCompleteClosureRecovery())
            await CompleteRecoveryAsync(database, operation, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return Result(operation.State == LifecycleOperationState.Failed
                ? ClosureProgressStatus.Recovered : ClosureProgressStatus.RecoveryStarted,
            operation.Id, operation.Revision, operation.FailureCode);
    }, cancellationToken);

    public Task<ClosureProgressResult> AcceptReleasedAsync(OrganisationClosureFenceReleasedV1 released,
        CancellationToken cancellationToken) => ExecuteAsync(released.Header.OperationId,
        released.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, released.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ClosureProgressStatus.Rejected, released.Header.OperationId, 0);
        var dedupe = await Replay(database, released.Header,
            nameof(OrganisationClosureFenceReleasedV1), released, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision, operation.FailureCode);
        if (operation.State != LifecycleOperationState.ReleasingFence
            || released.Header.OperationRevision != operation.Revision
            || !Matches(operation, released.Header)
            || released.Header.CausationId != LifecycleMessageIdentityV1.ForPhase(
                operation.Id, released.Header.ParticipantId, "release-fence", operation.Revision))
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
        var receipt = await database.LifecycleClosureFenceReceipts.SingleOrDefaultAsync(x =>
            x.OperationId == operation.Id && x.ParticipantId == released.Header.ParticipantId,
            cancellationToken);
        if (receipt is null || !string.Equals(receipt.FenceToken, released.FenceToken, StringComparison.Ordinal))
            return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
        RecordInbox(database, released.Header,
            nameof(OrganisationClosureFenceReleasedV1), released);
        operation.RecordClosureReleased(released.Header.ParticipantId);
        if (operation.CanCompleteClosureRecovery())
            await CompleteRecoveryAsync(database, operation, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return Result(operation.State == LifecycleOperationState.Failed
                ? ClosureProgressStatus.Recovered : ClosureProgressStatus.AwaitingParticipants,
            operation.Id, operation.Revision, operation.FailureCode);
    }, cancellationToken);

    public async Task<IReadOnlyList<Guid>> RecoverableAsync(Guid organisationId,
        CancellationToken cancellationToken)
    {
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await InitializeAsync(database, transaction, organisationId, cancellationToken);
            return await database.Set<LifecycleOperation>().Where(x => x.IsActive
                    && x.Family == LifecycleOperationFamily.Termination)
                .OrderBy(x => x.RequestedAt).Select(x => x.Id).ToListAsync(cancellationToken);
        }
        finally { Clear(database); }
    }

    public Task<ClosureProgressResult> ResumeAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken) => ExecuteAsync(operationId, organisationId, async database =>
    {
        var operation = await Operation(database, operationId, cancellationToken);
        if (operation is null) return Result(ClosureProgressStatus.Rejected, operationId, 0);
        if (operation.State == LifecycleOperationState.Pending)
        {
            var organisation = await database.Organisations.SingleAsync(x => x.Id == organisationId, cancellationToken);
            var closingAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
            if (!organisation.BeginClosure(closingAt))
                return Result(ClosureProgressStatus.Conflict, operation.Id, operation.Revision);
            operation.BeginTermination(closingAt);
            EnqueueEnteringFence(database, operation);
        }
        if (operation.State == LifecycleOperationState.EnteringFence)
        {
            foreach (var participant in ClosureParticipants(operation).Where(x =>
                         x.State == LifecycleParticipantState.Requested
                         || x.State == LifecycleParticipantState.Failed && x.FailureRetryable == true))
                Enqueue(database, operation, nameof(CloseOrganisationParticipantV1),
                    new CloseOrganisationParticipantV1(
                        Header(operation, participant,
                            LifecycleMessageIdentityV1.ForPhase(operation.Id, participant.ParticipantId,
                                "enter-fence", operation.Revision), operation.Id),
                        operation.ClosingAt!.Value));
            await database.SaveChangesAsync(cancellationToken);
            return Result(ClosureProgressStatus.AwaitingParticipants, operation.Id,
                operation.Revision, operation.FailureCode);
        }
        if (operation.State == LifecycleOperationState.ReleasingFence)
        {
            foreach (var participant in ClosureParticipants(operation).Where(x =>
                         x.State == LifecycleParticipantState.ReleaseRequested))
            {
                var receipt = await database.LifecycleClosureFenceReceipts.SingleAsync(x =>
                    x.OperationId == operation.Id && x.ParticipantId == participant.ParticipantId,
                    cancellationToken);
                Enqueue(database, operation, nameof(ReleaseOrganisationClosureFenceV1),
                    new ReleaseOrganisationClosureFenceV1(
                        Header(operation, participant,
                            LifecycleMessageIdentityV1.ForPhase(operation.Id, participant.ParticipantId,
                                "release-fence", operation.Revision), receipt.MessageId),
                        receipt.FenceToken));
            }
            await database.SaveChangesAsync(cancellationToken);
            return Result(ClosureProgressStatus.RecoveryStarted, operation.Id, operation.Revision);
        }
        return Result(operation.State == LifecycleOperationState.Completed
                ? ClosureProgressStatus.Archived : ClosureProgressStatus.Replay,
            operation.Id, operation.Revision, operation.FailureCode);
    }, cancellationToken);

    private async Task CompleteRecoveryAsync(AuthDbContext database, LifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        var recoveredAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
        var organisation = await database.Organisations.SingleAsync(
            x => x.Id == operation.OrganisationId, cancellationToken);
        if (!organisation.RecoverClosure(recoveredAt))
            throw new InvalidOperationException("Only a Closing organisation can recover before archive.");
        operation.CompleteClosureRecovery(recoveredAt);
    }

    private async Task<ClosureProgressStatus?> Replay(AuthDbContext database,
        LifecycleMessageHeaderV1 header, string messageType, object payload,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload, Json))).ToLowerInvariant();
        var prior = await database.LifecycleInboxReceipts.SingleOrDefaultAsync(
            x => x.MessageId == header.MessageId, cancellationToken);
        return prior is null ? null
            : prior.OperationId == header.OperationId
                && prior.OrganisationId == header.OrganisationId
                && prior.MessageType == messageType
                && prior.PayloadSha256 == hash
                ? ClosureProgressStatus.Replay : ClosureProgressStatus.Conflict;
    }

    private void RecordInbox(AuthDbContext database, LifecycleMessageHeaderV1 header,
        string messageType, object payload)
    {
        var hash = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload, Json))).ToLowerInvariant();
        database.Add(LifecycleInboxReceipt.Create(header.MessageId, header.OperationId,
            header.OrganisationId, messageType, hash, clock.GetUtcNow()));
    }

    private async Task<ClosureProgressResult> ExecuteAsync(Guid operationId, Guid organisationId,
        Func<AuthDbContext, Task<ClosureProgressResult>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await database.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);
                    try
                    {
                        await InitializeAsync(database, transaction, organisationId, cancellationToken);
                        var result = await action(database);
                        await transaction.CommitAsync(cancellationToken);
                        return result;
                    }
                    finally { Clear(database); }
                });
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) { }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        return Result(ClosureProgressStatus.Unavailable, operationId, 0);
    }

    private static async Task<LifecycleOperation?> Operation(AuthDbContext database, Guid operationId,
        CancellationToken cancellationToken) => await database.Set<LifecycleOperation>()
        .Include(x => x.Participants).SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);

    private static LifecycleParticipant[] ClosureParticipants(LifecycleOperation operation) =>
        operation.Participants.Where(x => x.Family == LifecycleOperationFamily.Termination
                && x.CapabilityKey == OrganisationClosureCapabilityV1.Fence)
            .OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();

    private static bool Matches(LifecycleOperation operation, LifecycleMessageHeaderV1 header) =>
        operation.Id == header.OperationId
        && operation.OrganisationId == header.OrganisationId
        && header.CorrelationId == operation.Id
        && operation.Participants.Any(x => x.Family == LifecycleOperationFamily.Termination
            && x.CapabilityKey == OrganisationClosureCapabilityV1.Fence
            && x.ParticipantId == header.ParticipantId
            && x.ContractVersion == header.ContractVersion);

    private static LifecycleMessageHeaderV1 Header(LifecycleOperation operation,
        LifecycleParticipant participant, Guid messageId, Guid causationId) => new(
        operation.Id, operation.OrganisationId, operation.Revision, participant.ParticipantId,
        participant.ContractVersion, messageId, causationId, operation.Id);

    private static LifecycleMessageHeaderV1 FactHeader(LifecycleOperation operation,
        Guid messageId, Guid causationId) => new(operation.Id, operation.OrganisationId,
        operation.Revision, CoordinatorParticipant, LifecycleContractV1.Version,
        messageId, causationId, operation.Id);

    private void EnqueueEnteringFence(AuthDbContext database, LifecycleOperation operation)
    {
        var closingAt = operation.ClosingAt
            ?? throw new InvalidOperationException("ClosingAt must be durable before closure dispatch.");
        foreach (var participant in ClosureParticipants(operation))
        {
            var header = Header(operation, participant,
                LifecycleMessageIdentityV1.ForPhase(operation.Id, participant.ParticipantId,
                    "enter-fence", operation.Revision), operation.Id);
            Enqueue(database, operation, nameof(CloseOrganisationParticipantV1),
                new CloseOrganisationParticipantV1(header, closingAt));
        }
        var factHeader = FactHeader(operation,
            LifecycleMessageIdentityV1.ForPhase(operation.Id, CoordinatorParticipant,
                "closing-fact", operation.Revision),
            operation.Id);
        Enqueue(database, operation, nameof(OrganisationClosingV1),
            new OrganisationClosingV1(factHeader, closingAt, operation.RegistryRevision,
                operation.InventoryHash));
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    private static OrganisationClosureFenceReceiptV1 ToContract(LifecycleClosureFenceReceipt receipt) => new(
        new LifecycleMessageHeaderV1(receipt.OperationId, receipt.OrganisationId,
            receipt.OperationRevision, receipt.ParticipantId, receipt.ContractVersion,
            receipt.MessageId, receipt.CausationId, receipt.CorrelationId),
        receipt.FenceToken, receipt.FenceRevision, receipt.BoundaryEstablishedAt,
        receipt.ReceiptHash);

    private void Enqueue(AuthDbContext database, LifecycleOperation operation,
        string messageType, object payload)
    {
        var now = clock.GetUtcNow();
        database.OutboxMessages.Add(OutboxMessage.Create(messageType, LifecycleContractV1.Version,
            JsonSerializer.Serialize(payload, Json), now, now, operation.Id.ToString("D"),
            operation.OrganisationId));
    }

    private static ClosureProgressResult Result(ClosureProgressStatus status, Guid operationId,
        long revision, string? failureCode = null) => new(status, operationId, revision, failureCode);

    private static async Task InitializeAsync(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisation, CancellationToken cancellationToken)
    {
        if (organisation == Guid.Empty) throw new InvalidOperationException("Authorized organisation is required.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisation.ToString("D");
        command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
            throw new InvalidOperationException("Lifecycle context initialization failed.");
        database.LifecycleOrganisationId = organisation;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }

    private static void Clear(AuthDbContext database)
    {
        database.LifecycleTransaction = null;
        database.LifecycleOrganisationId = Guid.Empty;
    }
}
