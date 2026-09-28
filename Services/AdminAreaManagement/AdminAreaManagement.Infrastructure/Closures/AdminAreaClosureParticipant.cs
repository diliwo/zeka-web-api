using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdminAreaManagement.Application.Closures;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.Infrastructure.Persistence.Closures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Zeka.Lifecycle.Contracts;

namespace AdminAreaManagement.Infrastructure.Closures;

internal sealed class AdminAreaClosureParticipant(
    ApplicationDbContext database,
    ITenantTransactionExecutor transactions,
    TenantTransactionAttemptState attempt,
    AdminAreaClosureFixtureScope fixture,
    TimeProvider clock) : IAdminAreaClosureParticipant
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrganisationClosureParticipantCompletedV1> EnterFenceAsync(
        CloseOrganisationParticipantV1 command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        DemandHeader(command.Header);
        DemandMessageIdentity(command.Header, "enter-fence");
        fixture.Demand(command.Header.OrganisationId);
        return EnterImmediateFenceAsync(command, cancellationToken);
    }

    private Task<OrganisationClosureParticipantCompletedV1> EnterImmediateFenceAsync(
        CloseOrganisationParticipantV1 command, CancellationToken cancellationToken) =>
        transactions.ExecuteAsync(async token =>
        {
            await AdminAreaClosureGate.AcquireOrganisationLockAsync(
                database, attempt, command.Header.OrganisationId, token);
            var duplicate = await DemandInboxReplayAsync(command.Header, "enter-fence", command.PayloadHash, token);
            var existing = await FindFenceAsync(command.Header, token);
            if (duplicate)
                return Completed(command.Header, existing
                    ?? throw new InvalidOperationException("Inbox replay is missing its durable closure fence."));

            if (existing is not null)
            {
                DemandSameRequest(existing, command);
                var replay = Completed(command.Header, existing);
                await RecordInboxAndOutboxAsync(command.Header, "enter-fence", command.PayloadHash,
                    replay.Header, replay, token);
                return replay;
            }

            await DemandDocumentEffectsDrainedAsync(token);
            var fence = await CreateFenceAsync(command, token);
            var response = Completed(command.Header, fence);
            await RecordInboxAndOutboxAsync(command.Header, "enter-fence", command.PayloadHash,
                response.Header, response, token);
            return response;
        }, cancellationToken);

    public Task<OrganisationClosureFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationClosureFenceV1 command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        DemandHeader(command.Header);
        DemandMessageIdentity(command.Header, "release-fence");
        fixture.Demand(command.Header.OrganisationId);
        return transactions.ExecuteAsync(async token =>
        {
            await AdminAreaClosureGate.AcquireOrganisationLockAsync(
                database, attempt, command.Header.OrganisationId, token);
            var duplicate = await DemandInboxReplayAsync(command.Header, "release-fence", command.PayloadHash, token);
            var fence = await database.AdminAreaClosureFences.SingleOrDefaultAsync(x =>
                x.OperationId == command.Header.OperationId
                && x.ParticipantId == command.Header.ParticipantId, token)
                ?? throw new InvalidOperationException("The closure fence does not exist.");
            var completedType = typeof(OrganisationClosureParticipantCompletedV1).FullName!;
            var acceptedEnterReceipt = await database.AdminAreaClosureOutbox.AsNoTracking()
                .Where(x => x.OperationId == command.Header.OperationId
                    && x.ParticipantId == command.Header.ParticipantId
                    && x.MessageType == completedType)
                .OrderBy(x => x.OccurredAt).ThenBy(x => x.MessageId)
                .Select(x => (Guid?)x.MessageId).FirstOrDefaultAsync(token)
                ?? throw new InvalidOperationException("The accepted closure receipt is missing.");
            if (command.Header.OperationRevision != checked(fence.OperationRevision + 1)
                || fence.ContractVersion != command.Header.ContractVersion
                || command.Header.CausationId != acceptedEnterReceipt
                || !StringComparer.Ordinal.Equals(fence.FenceToken, command.FenceToken))
                throw new InvalidOperationException("The release command does not match the durable closure fence.");
            if (duplicate)
                return Released(command.Header, fence);

            if (fence.ReleasedAt is null)
                await ReleaseThroughOwnerFunctionAsync(command, fence, token);
            var response = Released(command.Header, fence);
            await RecordInboxAndOutboxAsync(command.Header, "release-fence", command.PayloadHash,
                response.Header, response, token);
            return response;
        }, cancellationToken);
    }

    private Task<AdminAreaClosureFence?> FindFenceAsync(
        LifecycleMessageHeaderV1 header, CancellationToken cancellationToken) =>
        database.AdminAreaClosureFences.SingleOrDefaultAsync(x =>
            x.OperationId == header.OperationId && x.ParticipantId == header.ParticipantId, cancellationToken);

    private async Task DemandDocumentEffectsDrainedAsync(CancellationToken cancellationToken)
    {
        if (await database.DocumentPartners.AsNoTracking().AnyAsync(x =>
                x.FileWriteState != DocumentFileOperationState.Completed
                || (x.FileDeleteState != null && x.FileDeleteState != DocumentFileOperationState.Completed),
                cancellationToken))
            throw new InvalidOperationException(
                "Pre-boundary document file operations must drain before the closure fence can be established.");
    }

    private async Task ReleaseThroughOwnerFunctionAsync(ReleaseOrganisationClosureFenceV1 command,
        AdminAreaClosureFence fence, CancellationToken cancellationToken)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Closure release requires an active database transaction.");
        await using var release = database.Database.GetDbConnection().CreateCommand();
        release.Transaction = transaction;
        release.CommandText = """
            SELECT zeka.release_adminarea_closure_fence(
              @operation,@organisation,@participant,@revision,@token,@contract,
              @message,@causation,@correlation,@released_at)
            """;
        release.Parameters.Add(new Npgsql.NpgsqlParameter("operation", command.Header.OperationId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("organisation", command.Header.OrganisationId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("participant", command.Header.ParticipantId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter<long>("revision", command.Header.OperationRevision));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("token", command.FenceToken));
        release.Parameters.Add(new Npgsql.NpgsqlParameter<int>("contract", command.Header.ContractVersion));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("message", command.Header.MessageId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("causation", command.Header.CausationId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("correlation", command.Header.CorrelationId));
        release.Parameters.Add(new Npgsql.NpgsqlParameter("released_at",
            LifecycleContractTimeV1.Normalize(clock.GetUtcNow())));
        var result = await release.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Closure release did not return a timestamp.");
        var releasedAt = result switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("Closure release returned an invalid timestamp.")
        };
        database.Entry(fence).State = EntityState.Detached;
        fence.Release(LifecycleContractTimeV1.Normalize(releasedAt));
    }

    private async Task<AdminAreaClosureFence> CreateFenceAsync(
        CloseOrganisationParticipantV1 command, CancellationToken cancellationToken)
    {
        var active = await database.AdminAreaClosureFences.SingleOrDefaultAsync(x =>
            x.ParticipantId == command.Header.ParticipantId && x.ReleasedAt == null, cancellationToken);
        if (active is not null)
            throw new InvalidOperationException(
                "Another closure fence is active for this organisation and participant.");
        var fenceRevision = (await database.AdminAreaClosureFences
            .Where(x => x.ParticipantId == command.Header.ParticipantId)
            .MaxAsync(x => (long?)x.FenceRevision, cancellationToken) ?? 0) + 1;
        var now = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
        var closingAt = LifecycleContractTimeV1.Normalize(command.ClosingAt);
        var boundaryEstablishedAt = now >= closingAt ? now : closingAt;
        var fence = new AdminAreaClosureFence(
            command.Header.OperationId, command.Header.OrganisationId, command.Header.OperationRevision,
            command.Header.ParticipantId, command.Header.ContractVersion, command.PayloadHash,
            DeterministicFenceToken(command.Header, fenceRevision), fenceRevision,
            boundaryEstablishedAt);
        database.AdminAreaClosureFences.Add(fence);
        return fence;
    }

    private async Task<bool> DemandInboxReplayAsync(LifecycleMessageHeaderV1 header, string commandType,
        string requestHash, CancellationToken cancellationToken)
    {
        var existing = await database.AdminAreaClosureInbox.AsNoTracking().SingleOrDefaultAsync(
            x => x.MessageId == header.MessageId, cancellationToken);
        if (existing is null) return false;
        if (existing.OperationId != header.OperationId
            || existing.ParticipantId != header.ParticipantId
            || existing.CommandType != commandType
            || !StringComparer.Ordinal.Equals(existing.RequestHash, requestHash))
            throw new InvalidOperationException("A closure message identity was reused for different content.");
        return true;
    }

    private async Task RecordInboxAndOutboxAsync<T>(LifecycleMessageHeaderV1 header, string commandType,
        string requestHash, LifecycleMessageHeaderV1 responseHeader, T response,
        CancellationToken cancellationToken)
    {
        var occurredAt = LifecycleContractTimeV1.Normalize(clock.GetUtcNow());
        database.AdminAreaClosureInbox.Add(new AdminAreaClosureInbox(header.MessageId,
            header.OrganisationId, header.OperationId, header.ParticipantId, commandType, requestHash, occurredAt));
        database.AdminAreaClosureOutbox.Add(new AdminAreaClosureOutbox(
            responseHeader.MessageId, header.OrganisationId,
            header.OperationId, header.ParticipantId, response!.GetType().FullName!,
            JsonSerializer.Serialize(response, Json), occurredAt));
        await database.SaveChangesAsync(cancellationToken);
    }

    private static void DemandSameRequest(AdminAreaClosureFence fence, CloseOrganisationParticipantV1 command)
    {
        if (fence.OperationRevision != command.Header.OperationRevision
            || fence.ContractVersion != command.Header.ContractVersion
            || !StringComparer.Ordinal.Equals(fence.RequestHash, command.PayloadHash))
            throw new InvalidOperationException("The operation identity conflicts with the durable closure fence.");
    }

    private static void DemandHeader(LifecycleMessageHeaderV1 header)
    {
        if (!AdminAreaClosureContractV1.Owns(header.ParticipantId))
            throw new InvalidOperationException("The closure command targets an unknown AdminArea participant.");
        if (header.ContractVersion != LifecycleContractV1.Version)
            throw new InvalidOperationException("The closure contract version is not supported.");
    }

    private static void DemandMessageIdentity(LifecycleMessageHeaderV1 header, string phase)
    {
        var expected = LifecycleMessageIdentityV1.ForPhase(
            header.OperationId, header.ParticipantId, phase, header.OperationRevision);
        if (header.MessageId != expected)
            throw new InvalidOperationException("The closure command message identity is not canonical.");
        if (phase == "enter-fence"
            && (header.CausationId != header.OperationId || header.CorrelationId != header.OperationId))
            throw new InvalidOperationException("The closure enter command lineage is not canonical.");
    }

    private static OrganisationClosureParticipantCompletedV1 Completed(
        LifecycleMessageHeaderV1 header, AdminAreaClosureFence fence) =>
        new(ResponseHeader(header, typeof(OrganisationClosureParticipantCompletedV1)),
            fence.FenceToken, fence.FenceRevision, fence.EnteredAt);

    private static OrganisationClosureFenceReleasedV1 Released(
        LifecycleMessageHeaderV1 header, AdminAreaClosureFence fence) =>
        new(ResponseHeader(header, typeof(OrganisationClosureFenceReleasedV1)), fence.FenceToken, fence.ReleasedAt
            ?? throw new InvalidOperationException("The closure fence has not been released."));

    private static LifecycleMessageHeaderV1 ResponseHeader(LifecycleMessageHeaderV1 request, Type responseType)
    {
        var phase = responseType == typeof(OrganisationClosureParticipantCompletedV1)
            ? "enter-completed"
            : responseType == typeof(OrganisationClosureFenceReleasedV1)
                ? "release-completed"
                : throw new ArgumentOutOfRangeException(nameof(responseType), responseType,
                    "The closure response type is not supported.");

        return new(request.OperationId, request.OrganisationId, request.OperationRevision, request.ParticipantId,
            request.ContractVersion,
            LifecycleMessageIdentityV1.ForPhase(request.OperationId, request.ParticipantId, phase,
                request.OperationRevision),
            request.MessageId, request.CorrelationId);
    }

    private static string DeterministicFenceToken(LifecycleMessageHeaderV1 header, long revision)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"admin-area-closure-v1\n{header.OperationId:D}\n{header.OrganisationId:D}\n{header.OperationRevision}\n{header.ParticipantId}\n{revision}"));
        return "admin-area-closure-" + Convert.ToHexString(bytes).ToLowerInvariant()[..32];
    }

}
