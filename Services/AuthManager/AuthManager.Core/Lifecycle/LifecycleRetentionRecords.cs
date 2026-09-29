using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

public enum RetentionDecisionCode { Retain, Purge, Held, Unknown, Blocked }

/// <summary>An immutable policy evaluation, including evaluations that correctly block readiness.</summary>
public sealed class RetentionDecisionSet : ITenantOwnedEntity
{
    private RetentionDecisionSet() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long OperationRevision { get; private set; }
    public Guid RegistryRevision { get; private set; }
    public string InventoryHash { get; private set; } = "";
    public string DispositionInventoryHash { get; private set; } = "";
    public string PolicyId { get; private set; } = "";
    public string PolicyVersion { get; private set; } = "";
    public DateTimeOffset EvaluatedAt { get; private set; }
    public string SetHash { get; private set; } = "";
    public bool Ready { get; private set; }

    public static RetentionDecisionSet Create(Guid id, LifecycleOperation operation,
        string policyId, string policyVersion, DateTimeOffset evaluatedAt, string setHash, bool ready)
    {
        if (id == Guid.Empty || operation.Family != LifecycleOperationFamily.Termination
            || operation.DispositionInventoryHash is null || operation.State != LifecycleOperationState.Archived
            || operation.ArchivedAt is null || evaluatedAt.Offset != TimeSpan.Zero
            || evaluatedAt < operation.ArchivedAt || !Valid(policyId) || !Valid(policyVersion)
            || !Sha256(setHash))
            throw new ArgumentException("Retention decision-set identity is invalid.");
        return new()
        {
            Id = id, OperationId = operation.Id, OrganisationId = operation.OrganisationId,
            OperationRevision = operation.Revision, RegistryRevision = operation.RegistryRevision,
            InventoryHash = operation.InventoryHash,
            DispositionInventoryHash = operation.DispositionInventoryHash,
            PolicyId = policyId, PolicyVersion = policyVersion, EvaluatedAt = evaluatedAt,
            SetHash = setHash, Ready = ready
        };
    }

    private static bool Valid(string value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= 200;
    private static bool Sha256(string value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>One immutable category result belonging to an exact decision set.</summary>
public sealed class RetentionDecisionRecord : ITenantOwnedEntity
{
    private RetentionDecisionRecord() { }
    public Guid SetId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string Category { get; private set; } = "";
    public string ParticipantId { get; private set; } = "";
    public int ContractVersion { get; private set; }
    public string PolicyId { get; private set; } = "";
    public string PolicyVersion { get; private set; } = "";
    public DateTimeOffset DecidedAt { get; private set; }
    public DateTimeOffset ValidUntil { get; private set; }
    public RetentionDecisionCode Decision { get; private set; }
    public DateTimeOffset? EligibleAt { get; private set; }
    public string? HoldReference { get; private set; }
    public string ReasonCode { get; private set; } = "";

    public static RetentionDecisionRecord Create(RetentionDecisionSet set, string category,
        string participantId, int contractVersion, DateTimeOffset decidedAt,
        DateTimeOffset validUntil, RetentionDecisionCode decision, DateTimeOffset? eligibleAt,
        string? holdReference, string reasonCode)
    {
        if (!Valid(category) || !Valid(participantId) || !Valid(reasonCode)
            || contractVersion != 1 || !Enum.IsDefined(decision)
            || decidedAt == default || decidedAt.Offset != TimeSpan.Zero
            || validUntil.Offset != TimeSpan.Zero || validUntil < decidedAt
            || eligibleAt is { } eligible && eligible.Offset != TimeSpan.Zero
            || (decision == RetentionDecisionCode.Purge) != (eligibleAt is not null)
            || decision == RetentionDecisionCode.Held && !Valid(holdReference)
            || decision != RetentionDecisionCode.Held && holdReference is not null)
            throw new ArgumentException("Retention category decision is invalid.");
        return new()
        {
            SetId = set.Id, OperationId = set.OperationId, OrganisationId = set.OrganisationId,
            Category = category, ParticipantId = participantId, ContractVersion = contractVersion,
            PolicyId = set.PolicyId, PolicyVersion = set.PolicyVersion,
            DecidedAt = decidedAt, ValidUntil = validUntil, Decision = decision,
            EligibleAt = eligibleAt, HoldReference = holdReference, ReasonCode = reasonCode
        };
    }

    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= 200;
}
