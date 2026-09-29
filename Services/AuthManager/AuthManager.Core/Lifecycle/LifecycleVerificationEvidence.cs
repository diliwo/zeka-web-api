using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

/// <summary>Exact issued verification challenge; a public-contract command alone has no authority.</summary>
public sealed class LifecycleVerificationCommand : ITenantOwnedEntity
{
    private LifecycleVerificationCommand() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid PlanId { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string ItemId { get; private set; } = "";
    public string CommandHash { get; private set; } = "";
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public static LifecycleVerificationCommand Create(Guid messageId, Guid operationId,
        Guid organisationId, Guid planId, string participantId, string category,
        string itemId, string commandHash, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        if (messageId == Guid.Empty || operationId == Guid.Empty || organisationId == Guid.Empty
            || planId == Guid.Empty || string.IsNullOrWhiteSpace(participantId)
            || string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(itemId)
            || commandHash.Length != 64 || issuedAt >= expiresAt)
            throw new InvalidOperationException("Invalid verification challenge.");
        return new LifecycleVerificationCommand
        {
            MessageId = messageId, OperationId = operationId,
            OrganisationId = organisationId, PlanId = planId,
            ParticipantId = participantId, Category = category,
            ItemId = itemId, CommandHash = commandHash,
            IssuedAt = issuedAt, ExpiresAt = expiresAt
        };
    }
}

/// <summary>Surviving scoped postcondition evidence, independent of destructive progress.</summary>
public sealed class LifecycleVerificationEvidence : ITenantOwnedEntity
{
    private LifecycleVerificationEvidence() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid PlanId { get; private set; }
    public string Category { get; private set; } = "";
    public string ParticipantId { get; private set; } = "";
    public Guid CommandMessageId { get; private set; }
    public string CommandHash { get; private set; } = "";
    public Guid ReceiptId { get; private set; }
    public string ReceiptHash { get; private set; } = "";
    public string EvidenceHash { get; private set; } = "";
    public string VerifierVersion { get; private set; } = "";
    public DateTimeOffset ObservedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public int EligibleResidualCount { get; private set; }
    public int RetainedPresentCount { get; private set; }
    public int RetainedExpectedCount { get; private set; }
    public int? FileResidualCount { get; private set; }
    public bool PostconditionSatisfied { get; private set; }

    public static LifecycleVerificationEvidence Create(LifecycleVerificationObservation observation)
    {
        return new LifecycleVerificationEvidence
        {
            OperationId = observation.OperationId,
            OrganisationId = observation.OrganisationId,
            PlanId = observation.PlanId,
            Category = observation.Category,
            ParticipantId = observation.ParticipantId,
            CommandMessageId = observation.CommandMessageId,
            CommandHash = observation.CommandHash,
            ReceiptId = observation.ReceiptId,
            ReceiptHash = observation.ReceiptHash,
            EvidenceHash = observation.EvidenceHash,
            VerifierVersion = observation.VerifierVersion,
            ObservedAt = observation.ObservedAt,
            ExpiresAt = observation.ExpiresAt,
            EligibleResidualCount = observation.EligibleResidualCount,
            RetainedPresentCount = observation.RetainedPresentCount,
            RetainedExpectedCount = observation.RetainedExpectedCount,
            FileResidualCount = observation.FileResidualCount,
            PostconditionSatisfied = observation.PostconditionSatisfied
        };
    }

    public bool IsExactReplay(LifecycleVerificationEvidence next) =>
        CommandMessageId == next.CommandMessageId && CommandHash == next.CommandHash
        && ReceiptId == next.ReceiptId && ReceiptHash == next.ReceiptHash;

    public void ReplaceWithFresh(LifecycleVerificationEvidence next)
    {
        if (OperationId != next.OperationId || OrganisationId != next.OrganisationId
            || PlanId != next.PlanId || Category != next.Category
            || ParticipantId != next.ParticipantId || next.ObservedAt <= ObservedAt
            || next.CommandMessageId == CommandMessageId)
            throw new InvalidOperationException("Fresh verification must preserve frozen identity and advance observation time.");
        CommandMessageId = next.CommandMessageId;
        CommandHash = next.CommandHash;
        ReceiptId = next.ReceiptId;
        ReceiptHash = next.ReceiptHash;
        EvidenceHash = next.EvidenceHash;
        VerifierVersion = next.VerifierVersion;
        ObservedAt = next.ObservedAt;
        ExpiresAt = next.ExpiresAt;
        EligibleResidualCount = next.EligibleResidualCount;
        RetainedPresentCount = next.RetainedPresentCount;
        RetainedExpectedCount = next.RetainedExpectedCount;
        FileResidualCount = next.FileResidualCount;
        PostconditionSatisfied = next.PostconditionSatisfied;
    }
}

public sealed record LifecycleVerificationObservation(Guid OperationId, Guid OrganisationId,
    Guid PlanId, string Category, string ParticipantId, Guid CommandMessageId,
    string CommandHash, Guid ReceiptId, string ReceiptHash, string EvidenceHash,
    string VerifierVersion, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt,
    int EligibleResidualCount, int RetainedPresentCount, int RetainedExpectedCount,
    int? FileResidualCount, bool PostconditionSatisfied);
