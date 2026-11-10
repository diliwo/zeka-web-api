using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Application.BeneficiaryB1;

public sealed record BeneficiaryContextV1(int ContractVersion, int BeneficiaryId, string DisplayName,
    string CurrentCaseState, AudienceV1 Audience, bool CanRecordInteraction);

public sealed record AudienceV1(string Code, string Text);

public sealed record InteractionV1(int InteractionId, DateTime OccurredAtUtc, DateTime CreatedAtUtc,
    Guid AuthorMembershipId, int CurrentRevision, string ConciseNote);

public sealed record InteractionPageV1(int ContractVersion, IReadOnlyList<InteractionV1> Items,
    string? NextCursor, bool HasMore);

public sealed record InteractionRevisionsV1(int ContractVersion, int InteractionId,
    IReadOnlyList<InteractionV1> Revisions);

public sealed record RecordInteractionV1(int ContractVersion, int InteractionId, int Revision,
    DateTime CreatedAtUtc);

public sealed record RecordLinkedInteractionV1(int ContractVersion, int InteractionId, int Revision,
    int FollowUpActionId);

public sealed class BeneficiaryWireException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IBeneficiaryB1Store
{
    Task<BeneficiaryContextV1> ContextAsync(int beneficiaryId, bool canEditAll,
        bool canEditAssigned, Guid actorMembershipId, CancellationToken token);
    Task<InteractionPageV1> RecentAsync(int beneficiaryId, int limit, string? cursor, CancellationToken token);
    Task<InteractionV1> DirectAsync(int beneficiaryId, int interactionId, CancellationToken token);
    Task<RecordInteractionV1> RecordAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token);
    Task<RecordInteractionV1?> ReconcileAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token);
    // Called only inside the linked executor's guarded tenant reads.
    Task<RecordInteractionCommand?> RecoverLinkedCommandAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, FollowUpActionInput action, Guid actorMembershipId,
        CancellationToken token) => throw new TenantAccessException(AccessFailure.Unavailable);
    Task<RecordInteractionCommand> ResolveLinkedCommandAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, FollowUpActionInput action, DateOnly localDay,
        CancellationToken token) => throw new TenantAccessException(AccessFailure.Unavailable);
}

// Only an isolated test-host controller calls this B1 surface. There is no production route.
public sealed class BeneficiaryB1Service(IBeneficiaryB1Store store, TenantOperation operation,
    ITenantTransactionExecutor transactions, TimeProvider clock, LinkedInteractionExecutor? linked = null)
{
    private static readonly RequiresTenantPermissionAttribute Edit =
        new("Clients.EditAll", "Clients.EditAssigned");

    public async Task<BeneficiaryContextV1> ContextAsync(int beneficiaryId, CancellationToken token)
    {
        await operation.AuthorizeBeneficiaryReadAsync(token);
        return await transactions.ExecuteOnceAsync(t => store.ContextAsync(beneficiaryId,
            operation.CanEditAll, operation.CanEditAssigned, operation.MembershipId, t), token);
    }

    public async Task<InteractionPageV1> RecentAsync(int beneficiaryId, int limit,
        string? cursor, CancellationToken token)
    {
        await operation.AuthorizeBeneficiaryReadAsync(token);
        return await transactions.ExecuteOnceAsync(t => store.RecentAsync(beneficiaryId, limit, cursor, t), token);
    }

    public async Task<InteractionV1> DirectAsync(int beneficiaryId, int interactionId, CancellationToken token)
    {
        await operation.AuthorizeBeneficiaryReadAsync(token);
        return await transactions.ExecuteOnceAsync(t => store.DirectAsync(beneficiaryId, interactionId, t), token);
    }

    public async Task<InteractionRevisionsV1> RevisionsAsync(int beneficiaryId, int interactionId,
        CancellationToken token)
    {
        var original = await DirectAsync(beneficiaryId, interactionId, token);
        return new InteractionRevisionsV1(1, interactionId, [original]);
    }

    public async Task<RecordInteractionV1> RecordAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, CancellationToken token)
    {
        Validate(beneficiaryId, occurredAtUtc, note, idempotencyKey, clock.GetUtcNow());
        await operation.AuthorizeAsync(Edit, token);
        try
        {
            return await transactions.ExecuteOrdinaryOnceAsync(t => store.RecordAsync(beneficiaryId,
                occurredAtUtc, note, idempotencyKey, operation.MembershipId, t), token);
        }
        catch (TenantCommitIndeterminateException)
        {
            var saved = await transactions.ExecuteOrdinaryOnceAsync(t => store.ReconcileAsync(beneficiaryId,
                occurredAtUtc, note, idempotencyKey, operation.MembershipId, t), token);
            if (saved is null) throw;
            return saved;
        }
    }

    public async Task<RecordLinkedInteractionV1> RecordLinkedAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, FollowUpActionInput action, CancellationToken token)
    {
        Validate(beneficiaryId, occurredAtUtc, note, idempotencyKey, clock.GetUtcNow());
        ValidateAction(action);
        var executor = linked ?? throw new TenantAccessException(AccessFailure.Unavailable);
        var result = await executor.ExecuteAsync(
            t => store.RecoverLinkedCommandAsync(beneficiaryId, occurredAtUtc, note,
                idempotencyKey, action, operation.MembershipId, t),
            (day, t) => store.ResolveLinkedCommandAsync(beneficiaryId, occurredAtUtc, note,
                idempotencyKey, action, day, t), token);
        if (result.InteractionId <= 0 || result.Revision != 1
            || result.FollowUpActionId is not int actionId || actionId <= 0)
            throw new TenantAccessException(AccessFailure.Unavailable);
        return new RecordLinkedInteractionV1(1, result.InteractionId, result.Revision, actionId);
    }

    public static void Validate(int beneficiaryId, DateTime occurredAtUtc, string note,
        string idempotencyKey, DateTimeOffset now)
    {
        if (beneficiaryId <= 0 || note is null || note.Length is < 1 or > 1000
            || string.IsNullOrWhiteSpace(note) || note.Contains('\0') || !WellFormedUtf16(note)
            || occurredAtUtc.Kind != DateTimeKind.Utc
            || occurredAtUtc.Ticks % TimeSpan.TicksPerMillisecond != 0
            || occurredAtUtc > now.UtcDateTime.AddMinutes(5)
            || idempotencyKey is null || idempotencyKey.Length != 36
            || !Guid.TryParseExact(idempotencyKey, "D", out var key)
            || key.ToString("D") != idempotencyKey
            || idempotencyKey[14] != '4'
            || idempotencyKey[19] is not ('8' or '9' or 'a' or 'b'))
            throw new BeneficiaryWireException("INVALID_REQUEST");
    }

    public static void ValidateAction(FollowUpActionInput? action)
    {
        if (action is null || action.Purpose is null || action.Purpose.Length is < 1 or > 500
            || string.IsNullOrWhiteSpace(action.Purpose) || !WellFormedUtf16(action.Purpose)
            || action.Purpose.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))
            || action.DueLocalDate == default || action.AssigneeMembershipId == Guid.Empty)
            throw new BeneficiaryWireException("INVALID_REQUEST");
    }

    private static bool WellFormedUtf16(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
            }
            else if (char.IsLowSurrogate(text[index])) return false;
        }
        return true;
    }
}
