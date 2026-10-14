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

public sealed class BeneficiaryWireException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IBeneficiaryB1Store
{
    Task<BeneficiaryContextV1> ContextAsync(int beneficiaryId, bool canEdit, CancellationToken token);
    Task<InteractionPageV1> RecentAsync(int beneficiaryId, int limit, string? cursor, CancellationToken token);
    Task<InteractionV1> DirectAsync(int beneficiaryId, int interactionId, CancellationToken token);
    Task<RecordInteractionV1> RecordAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token);
    Task<RecordInteractionV1?> ReconcileAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token);
}

// Only an isolated test-host controller calls this B1 surface. There is no production route.
public sealed class BeneficiaryB1Service(IBeneficiaryB1Store store, TenantOperation operation,
    ITenantTransactionExecutor transactions, TimeProvider clock)
{
    private static readonly RequiresTenantPermissionAttribute Edit =
        new("Clients.EditAll", "Clients.EditAssigned");

    public async Task<BeneficiaryContextV1> ContextAsync(int beneficiaryId, CancellationToken token)
    {
        await operation.AuthorizeBeneficiaryReadAsync(token);
        return await transactions.ExecuteOnceAsync(t => store.ContextAsync(beneficiaryId,
            operation.CanEditDossier, t), token);
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

    public static void Validate(int beneficiaryId, DateTime occurredAtUtc, string note,
        string idempotencyKey, DateTimeOffset now)
    {
        if (beneficiaryId <= 0 || note is null || note.Length is < 1 or > 1000
            || string.IsNullOrWhiteSpace(note) || !WellFormedUtf16(note)
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
