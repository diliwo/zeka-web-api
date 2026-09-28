using System.Text;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Lifecycle;
using ClientManagement.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace ClientManagement.Infrastructure.Persistence.Lifecycle;

public sealed class ClientOrganisationClosureParticipant(
    ApplicationDbContext database,
    ITenantTransactionExecutor transactions,
    IClientClosureFixtureScope fixtureScope,
    IClientClosureRecoveryCapability recoveryCapability,
    TimeProvider clock) : IClientOrganisationClosureParticipant
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrganisationClosureParticipantCompletedV1> EnterFenceAsync(
        CloseOrganisationParticipantV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateHeader(command.Header, "enter-fence");
        fixtureScope.Demand(command.Header.OrganisationId);
        return ExecuteReplaySafeAsync(async token =>
        {
            await AcquireBarrierLock(command.Header.OrganisationId, token);
            var replay = await Replay<OrganisationClosureParticipantCompletedV1>(
                command.Header, command.PayloadHash, token);
            if (replay is not null) return replay;

            var fence = await database.OrganisationClosureFences.SingleOrDefaultAsync(
                value => value.ReleasedAt == null, token);
            if (fence is not null && fence.OperationId != command.Header.OperationId)
                throw new InvalidOperationException("organisation_closure_fence_active");
            if (fence is not null && (fence.OperationRevision != command.Header.OperationRevision
                || !StringComparer.Ordinal.Equals(fence.RequestHash, command.PayloadHash)))
                throw new InvalidOperationException("organisation_closure_operation_identity_conflict");

            var observedNow = clock.GetUtcNow();
            var now = LifecycleContractTimeV1.Normalize(
                observedNow >= command.ClosingAt ? observedNow : command.ClosingAt);
            fence ??= OrganisationClosureFence.Enter(
                command.Header.OperationId,
                command.Header.OrganisationId,
                command.Header.OperationRevision,
                ClientClosureContract.ParticipantId,
                command.PayloadHash,
                Guid.NewGuid().ToString("N"),
                now);
            if (database.Entry(fence).State == EntityState.Detached) database.Add(fence);

            var response = new OrganisationClosureParticipantCompletedV1(
                ReplyHeader(command.Header, "enter-completed"),
                fence.FenceToken,
                fence.FenceRevision,
                fence.EnteredAt);
            PersistReply(command.Header, command.PayloadHash, response, now);
            await database.SaveChangesAsync(token);
            return response;
        }, cancellationToken);
    }

    public async Task<OrganisationClosureFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationClosureFenceV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateHeader(command.Header, "release-fence");
        fixtureScope.Demand(command.Header.OrganisationId);
        await recoveryCapability.ReleaseAsync(command,
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow()), cancellationToken);
        return await ExecuteReplaySafeAsync(async token =>
        {
            await AcquireBarrierLock(command.Header.OrganisationId, token);
            var replay = await Replay<OrganisationClosureFenceReleasedV1>(
                command.Header, command.PayloadHash, token);
            if (replay is not null) return replay;

            if (await database.OrganisationClosureInbox.AsNoTracking().AnyAsync(
                value => value.OperationId == command.Header.OperationId
                    && value.OperationRevision == command.Header.OperationRevision
                    && value.ResponseType == typeof(OrganisationClosureFenceReleasedV1).FullName, token))
                throw new InvalidOperationException("organisation_closure_release_identity_conflict");

            var fence = await database.OrganisationClosureFences.AsNoTracking().SingleOrDefaultAsync(
                value => value.OperationId == command.Header.OperationId, token)
                ?? throw new InvalidOperationException("organisation_closure_fence_missing");
            if (command.Header.OperationRevision != checked(fence.OperationRevision + 1))
                throw new InvalidOperationException("organisation_closure_revision_conflict");
            if (!StringComparer.Ordinal.Equals(fence.FenceToken, command.FenceToken))
                throw new InvalidOperationException("organisation_closure_fence_token_invalid");

            var acceptedEnter = await database.OrganisationClosureInbox.AsNoTracking().SingleOrDefaultAsync(
                value => value.OperationId == fence.OperationId
                    && value.OperationRevision == fence.OperationRevision
                    && value.ResponseType == typeof(OrganisationClosureParticipantCompletedV1).FullName, token)
                ?? throw new InvalidOperationException("organisation_closure_enter_receipt_missing");
            var acceptedCompletion = JsonSerializer.Deserialize<OrganisationClosureParticipantCompletedV1>(
                acceptedEnter.ResponseJson, Json)
                ?? throw new InvalidOperationException("organisation_closure_enter_receipt_is_invalid");
            if (command.Header.CausationId != acceptedEnter.ResultMessageId
                || command.Header.CausationId != acceptedCompletion.Header.MessageId
                || command.Header.CorrelationId != acceptedCompletion.Header.CorrelationId)
                throw new InvalidOperationException("organisation_closure_recovery_identity_conflict");

            var releasedAt = fence.ReleasedAt
                ?? throw new InvalidOperationException("organisation_closure_recovery_capability_failed");
            var response = new OrganisationClosureFenceReleasedV1(
                ReplyHeader(command.Header, "release-completed"), fence.FenceToken, releasedAt);
            PersistReply(command.Header, command.PayloadHash, response, releasedAt);
            await database.SaveChangesAsync(token);
            return response;
        }, cancellationToken);
    }

    private async Task<T> ExecuteReplaySafeAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return await transactions.ExecuteLifecycleAsync(work, cancellationToken); }
            catch (Exception exception) when (IsRetryable(exception)) { }
        }
        throw new InvalidOperationException("Client closure participant could not reconcile a concurrent delivery.");
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure } => true,
        DbUpdateException { InnerException: not null } update => IsRetryable(update.InnerException!),
        InvalidOperationException { InnerException: not null } wrapper => IsRetryable(wrapper.InnerException!),
        _ => false
    };

    private async Task<T?> Replay<T>(LifecycleMessageHeaderV1 header, string requestHash, CancellationToken token)
        where T : class
    {
        var receipt = await database.OrganisationClosureInbox.AsNoTracking()
            .SingleOrDefaultAsync(value => value.MessageId == header.MessageId, token);
        if (receipt is null) return null;
        if (!StringComparer.Ordinal.Equals(receipt.RequestHash, requestHash)
            || !StringComparer.Ordinal.Equals(receipt.ResponseType, typeof(T).FullName))
            throw new InvalidOperationException("organisation_closure_message_identity_conflict");
        return JsonSerializer.Deserialize<T>(receipt.ResponseJson, Json)
            ?? throw new InvalidOperationException("organisation_closure_receipt_is_invalid");
    }

    private void PersistReply<T>(
        LifecycleMessageHeaderV1 request,
        string requestHash,
        T response,
        DateTimeOffset completedAt) where T : class
    {
        var responseHeader = response switch
        {
            OrganisationClosureParticipantCompletedV1 completed => completed.Header,
            OrganisationClosureFenceReleasedV1 released => released.Header,
            _ => throw new InvalidOperationException("Unsupported closure participant response.")
        };
        var json = JsonSerializer.Serialize(response, Json);
        database.Add(OrganisationClosureCommandReceipt.Complete(
            request.OrganisationId,
            request.MessageId,
            request.OperationId,
            request.OperationRevision,
            requestHash,
            typeof(T).FullName!,
            json,
            responseHeader.MessageId,
            completedAt));
        database.Add(OrganisationClosureOutboxMessage.Stage(
            request.OrganisationId,
            responseHeader.MessageId,
            request.OperationId,
            typeof(T).FullName!,
            json,
            ClientExportCanonical.Sha256(Encoding.UTF8.GetBytes(json)),
            completedAt));
    }

    private async Task AcquireBarrierLock(Guid organisationId, CancellationToken token)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("organisation_closure_transaction_missing");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation_id, 0))";
        AddParameter(command, "organisation_id", organisationId.ToString("D"));
        await command.ExecuteScalarAsync(token);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static LifecycleMessageHeaderV1 ReplyHeader(LifecycleMessageHeaderV1 request, string phase) =>
        new(request.OperationId, request.OrganisationId, request.OperationRevision,
            request.ParticipantId, request.ContractVersion,
            LifecycleMessageIdentityV1.ForPhase(
                request.OperationId, request.ParticipantId, phase, request.OperationRevision),
            request.MessageId, request.CorrelationId);

    private static void ValidateHeader(LifecycleMessageHeaderV1 header, string phase)
    {
        if (!StringComparer.Ordinal.Equals(header.ParticipantId, ClientClosureContract.ParticipantId)
            || header.ContractVersion != LifecycleContractV1.Version
            || header.MessageId != LifecycleMessageIdentityV1.ForPhase(
                header.OperationId, header.ParticipantId, phase, header.OperationRevision)
            || (phase == "enter-fence"
                && (header.CausationId != header.OperationId || header.CorrelationId != header.OperationId)))
            throw new InvalidOperationException("organisation_closure_participant_identity_invalid");
    }
}
