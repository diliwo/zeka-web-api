using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

/// <summary>
/// Immutable technical plan for a deterministic fixture. A production legal disposition
/// classification or authorization is deliberately not represented by this type.
/// </summary>
public sealed class LifecyclePurgePlan : ITenantOwnedEntity
{
    private LifecyclePurgePlan() { }
    public Guid Id { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long AdmittedOperationRevision { get; private set; }
    public Guid RegistryRevision { get; private set; }
    public string InventoryHash { get; private set; } = "";
    public string DispositionInventoryHash { get; private set; } = "";
    public Guid DecisionSetId { get; private set; }
    public string DecisionSetHash { get; private set; } = "";
    public DateTimeOffset PlannedAt { get; private set; }
    public string EntriesJson { get; private set; } = "";
    public string PlanHash { get; private set; } = "";
    public IReadOnlyList<PurgePlanEntry> Entries =>
        JsonSerializer.Deserialize<PurgePlanEntry[]>(EntriesJson) ?? [];

    public static LifecyclePurgePlan Create(Guid id, LifecycleOperation operation,
        RetentionDecisionSet set, IReadOnlyCollection<RetentionDecisionRecord> decisions,
        DateTimeOffset plannedAt)
    {
        if (id == Guid.Empty || plannedAt == default || plannedAt.Offset != TimeSpan.Zero
            || operation.Family != LifecycleOperationFamily.Termination
            || operation.State != LifecycleOperationState.DispositionReady || !operation.IsActive
            || operation.DispositionReadyAt is null || plannedAt < operation.DispositionReadyAt
            || operation.DispositionInventoryHash is null || operation.CompletedAt is not null
            || operation.RegistryRevision != ReviewedDispositionRegistryV1.Revision
            || !ReviewedDispositionRegistryV1.HasCompleteFrozenInventory(operation.Participants)
            || set.OperationId != operation.Id || set.OrganisationId != operation.OrganisationId
            || !set.Ready || set.RegistryRevision != operation.RegistryRevision
            || set.InventoryHash != operation.InventoryHash
            || set.DispositionInventoryHash != operation.DispositionInventoryHash
            || set.SetHash != operation.RetentionDecisionSetHash
            || set.OperationRevision + 1 != operation.Revision)
            throw new InvalidOperationException("Purge plan must bind the current ready operation and accepted set.");

        var categories = operation.Participants.Where(x => x.Family == LifecycleOperationFamily.Termination
                && x.CapabilityKey == ReviewedDispositionRegistryV1.CapabilityKey)
            .OrderBy(x => x.OwnershipScope, StringComparer.Ordinal).ToArray();
        if (decisions.Count != categories.Length || decisions.Select(x => x.Category)
                .Distinct(StringComparer.Ordinal).Count() != categories.Length)
            throw new InvalidOperationException("Every frozen disposition category needs one decision.");
        var entries = new List<PurgePlanEntry>(categories.Length);
        foreach (var category in categories)
        {
            var record = decisions.SingleOrDefault(x => x.Category == category.OwnershipScope);
            if (record is null || record.SetId != set.Id || record.OperationId != operation.Id
                || record.OrganisationId != operation.OrganisationId
                || record.ParticipantId != category.ParticipantId
                || record.ContractVersion != category.ContractVersion
                || record.PolicyId != set.PolicyId || record.PolicyVersion != set.PolicyVersion
                || record.DecidedAt > set.EvaluatedAt || record.ValidUntil <= plannedAt
                || record.Decision is not (RetentionDecisionCode.Purge or RetentionDecisionCode.Retain)
                || record.Decision == RetentionDecisionCode.Purge
                    && (record.EligibleAt is null || record.EligibleAt > plannedAt)
                || record.Decision == RetentionDecisionCode.Retain && record.EligibleAt is not null)
                throw new InvalidOperationException("Frozen category has no current eligible purge/retain decision.");
            entries.Add(new PurgePlanEntry(record.Category, record.ParticipantId,
                record.ContractVersion, record.Decision, $"fixture:{record.Category}"));
        }
        if (entries.All(x => x.Decision == RetentionDecisionCode.Retain))
            throw new InvalidOperationException("A plan without an eligible purge obligation cannot cross the boundary.");
        var json = JsonSerializer.Serialize(entries);
        var plan = new LifecyclePurgePlan
        {
            Id = id, OperationId = operation.Id, OrganisationId = operation.OrganisationId,
            AdmittedOperationRevision = operation.Revision,
            RegistryRevision = operation.RegistryRevision, InventoryHash = operation.InventoryHash,
            DispositionInventoryHash = operation.DispositionInventoryHash,
            DecisionSetId = set.Id, DecisionSetHash = set.SetHash,
            PlannedAt = plannedAt, EntriesJson = json
        };
        plan.PlanHash = Hash("zeka-purge-plan-v1", id, plan.OperationId, plan.OrganisationId,
            plan.AdmittedOperationRevision, plan.RegistryRevision, plan.InventoryHash,
            plan.DispositionInventoryHash, plan.DecisionSetId, plan.DecisionSetHash,
            plan.PlannedAt, json);
        return plan;
    }

    public string BoundaryEvidenceHash(DateTimeOffset irreversibleAt) =>
        Hash("zeka-purge-boundary-v1", OperationId, OrganisationId,
            checked(AdmittedOperationRevision + 1), PlanHash, DecisionSetHash, irreversibleAt);

    private static string Hash(params object[] values) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
}

public sealed record PurgePlanEntry(string Category, string ParticipantId,
    int ContractVersion, RetentionDecisionCode Decision, string ItemId);

public enum PurgeMessageKind { IrreversibleStarted = 1, ParticipantPurge = 2 }

/// <summary>Transport-neutral V1 identity; payload hashes are computed over canonical JSON.</summary>
public sealed record PurgeMessageV1(Guid MessageId, PurgeMessageKind Kind,
    Guid OrganisationId, Guid TerminationOperationId, long OperationRevision,
    Guid RegistryRevision, Guid PlanId, string PlanHash, Guid DecisionSetId,
    string DecisionSetHash, string ParticipantId, string CapabilityKey,
    string Category, string ItemId, Guid IdempotencyId)
{
    public static Guid StartId(Guid planId) => Identity("start", planId, "", "");
    public static Guid CommandId(Guid planId, string participantId, string category,
        string itemId) => Identity("participant", planId, participantId, category + "\n" + itemId);

    private static Guid Identity(string phase, Guid planId, string participantId, string category)
    {
        if (planId == Guid.Empty || phase == "participant"
            && (string.IsNullOrWhiteSpace(participantId) || string.IsNullOrWhiteSpace(category)))
            throw new ArgumentException("Purge message identity inputs are incomplete.");
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"zeka-purge-message-id-v1\n{phase}\n{planId:D}\n{participantId}\n{category}")))
            .ToLowerInvariant();
        return Guid.ParseExact($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}", "D");
    }

    public string CanonicalJson() => JsonSerializer.Serialize(new object[]
    {
        "zeka-purge-message-v1", MessageId, (int)Kind, OrganisationId,
        TerminationOperationId, OperationRevision, RegistryRevision, PlanId, PlanHash,
        DecisionSetId, DecisionSetHash, ParticipantId, CapabilityKey, Category, ItemId, IdempotencyId
    });

    public string PayloadHash() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(CanonicalJson()))).ToLowerInvariant();

    public static PurgeMessageV1 Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() != 16
            || document.RootElement[0].GetString() != "zeka-purge-message-v1")
            throw new InvalidOperationException("Unsupported purge command version or shape.");
        var fields = document.RootElement;
        var message = new PurgeMessageV1(fields[1].GetGuid(),
            (PurgeMessageKind)fields[2].GetInt32(), fields[3].GetGuid(), fields[4].GetGuid(),
            fields[5].GetInt64(), fields[6].GetGuid(), fields[7].GetGuid(),
            fields[8].GetString() ?? "", fields[9].GetGuid(), fields[10].GetString() ?? "",
            fields[11].GetString() ?? "", fields[12].GetString() ?? "",
            fields[13].GetString() ?? "", fields[14].GetString() ?? "", fields[15].GetGuid());
        if (message.CanonicalJson() != json || !Enum.IsDefined(message.Kind))
            throw new InvalidOperationException("Purge command canonical payload is invalid.");
        return message;
    }
}

public sealed class LifecyclePurgeOutboxMessage : ITenantOwnedEntity
{
    private LifecyclePurgeOutboxMessage() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid PlanId { get; private set; }
    public PurgeMessageKind Kind { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public string Category { get; private set; } = "";
    public string PayloadJson { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";

    public static LifecyclePurgeOutboxMessage From(PurgeMessageV1 message)
    {
        if (message.MessageId == Guid.Empty || message.TerminationOperationId == Guid.Empty
            || message.OrganisationId == Guid.Empty || message.PlanId == Guid.Empty
            || message.IdempotencyId == Guid.Empty || message.OperationRevision < 1
            || message.RegistryRevision != ReviewedDispositionRegistryV1.Revision
            || !Sha256(message.PlanHash) || !Sha256(message.DecisionSetHash)
            || message.MessageId != (message.Kind == PurgeMessageKind.IrreversibleStarted
                ? PurgeMessageV1.StartId(message.PlanId)
                : PurgeMessageV1.CommandId(message.PlanId, message.ParticipantId,
                    message.Category, message.ItemId))
            || message.Kind == PurgeMessageKind.IrreversibleStarted
                && (message.ParticipantId != "" || message.Category != "" || message.ItemId != "")
            || message.Kind == PurgeMessageKind.ParticipantPurge
                && (string.IsNullOrWhiteSpace(message.ParticipantId)
                    || message.CapabilityKey != ReviewedDispositionRegistryV1.CapabilityKey
                    || string.IsNullOrWhiteSpace(message.Category)
                    || string.IsNullOrWhiteSpace(message.ItemId)))
            throw new ArgumentException("Purge message identity is invalid.", nameof(message));
        return new()
        {
            MessageId = message.MessageId, OperationId = message.TerminationOperationId,
            OrganisationId = message.OrganisationId, PlanId = message.PlanId,
            Kind = message.Kind, ParticipantId = message.ParticipantId,
            Category = message.Category, PayloadJson = message.CanonicalJson(),
            PayloadHash = message.PayloadHash()
        };
    }

    public PurgeMessageV1 Decode()
    {
        var message = PurgeMessageV1.Parse(PayloadJson);
        if (message.PayloadHash() != PayloadHash || message.MessageId != MessageId
            || message.TerminationOperationId != OperationId
            || message.OrganisationId != OrganisationId || message.PlanId != PlanId
            || message.Kind != Kind || message.ParticipantId != ParticipantId
            || message.Category != Category)
            throw new InvalidOperationException("Purge outbox payload conflicts with its durable envelope.");
        return message;
    }

    private static bool Sha256(string value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public enum PurgeProgressState
{
    Pending = 1,
    Purged = 2,
    AlreadyAbsent = 3,
    FailedRetryable = 4,
    InterventionRequired = 5
}

public sealed record PurgeReceiptV1(Guid ReceiptId, Guid CommandMessageId,
    Guid OrganisationId, Guid TerminationOperationId, long IrreversibleRevision,
    Guid RegistryRevision, Guid PlanId, string PlanHash, Guid DecisionSetId,
    string DecisionSetHash, string ParticipantId, string CapabilityKey, string Category,
    string ItemId,
    Guid IdempotencyId, PurgeProgressState State, string EvidenceHash,
    string? SafeFailureCode, int ContractVersion = 1);

/// <summary>Coordinator projection of a service-owned durable participant/item ledger.</summary>
public sealed class LifecyclePurgeParticipantProgress : ITenantOwnedEntity
{
    private LifecyclePurgeParticipantProgress() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid PlanId { get; private set; }
    public string Category { get; private set; } = "";
    public string ItemId { get; private set; } = "";
    public string ParticipantId { get; private set; } = "";
    public Guid CommandMessageId { get; private set; }
    public string CommandHash { get; private set; } = "";
    public PurgeProgressState State { get; private set; }
    public Guid? LastReceiptId { get; private set; }
    public string? EvidenceHash { get; private set; }
    public string? SafeFailureCode { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public static LifecyclePurgeParticipantProgress Pending(LifecyclePurgePlan plan,
        PurgePlanEntry entry, PurgeMessageV1 command)
    {
        if (entry.Decision != RetentionDecisionCode.Purge
            || command.Kind != PurgeMessageKind.ParticipantPurge
            || command.TerminationOperationId != plan.OperationId
            || command.OrganisationId != plan.OrganisationId || command.PlanId != plan.Id
            || command.PlanHash != plan.PlanHash || command.ParticipantId != entry.ParticipantId
            || command.Category != entry.Category || command.ItemId != entry.ItemId)
            throw new ArgumentException("Purge command does not match the frozen plan.");
        return new()
        {
            OperationId = plan.OperationId, OrganisationId = plan.OrganisationId,
            PlanId = plan.Id, Category = entry.Category, ItemId = entry.ItemId,
            ParticipantId = entry.ParticipantId,
            CommandMessageId = command.MessageId, CommandHash = command.PayloadHash(),
            State = PurgeProgressState.Pending
        };
    }

    public bool Record(PurgeReceiptV1 receipt, LifecyclePurgePlan plan,
        long irreversibleRevision, DateTimeOffset observedAt)
    {
        if (receipt.ReceiptId == Guid.Empty || receipt.CommandMessageId != CommandMessageId
            || receipt.ContractVersion != 1
            || receipt.OrganisationId != OrganisationId || receipt.TerminationOperationId != OperationId
            || receipt.IrreversibleRevision != irreversibleRevision
            || receipt.RegistryRevision != plan.RegistryRevision || receipt.PlanId != PlanId
            || receipt.PlanHash != plan.PlanHash || receipt.DecisionSetId != plan.DecisionSetId
            || receipt.DecisionSetHash != plan.DecisionSetHash
            || receipt.ParticipantId != ParticipantId || receipt.Category != Category
            || receipt.ItemId != ItemId
            || receipt.CapabilityKey != ReviewedDispositionRegistryV1.CapabilityKey
            || receipt.IdempotencyId != CommandMessageId || !Sha256(receipt.EvidenceHash)
            || !Enum.IsDefined(receipt.State) || receipt.State == PurgeProgressState.Pending
            || observedAt == default || observedAt.Offset != TimeSpan.Zero
            || receipt.State is PurgeProgressState.FailedRetryable or PurgeProgressState.InterventionRequired
                && string.IsNullOrWhiteSpace(receipt.SafeFailureCode)
            || receipt.State is PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent
                && receipt.SafeFailureCode is not null)
            throw new InvalidOperationException("Purge receipt identity or outcome is invalid.");
        if (LastReceiptId == receipt.ReceiptId)
            return EvidenceHash == receipt.EvidenceHash && State == receipt.State
                && SafeFailureCode == receipt.SafeFailureCode;
        if (State is PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent)
            throw new InvalidOperationException("Completed purge progress cannot be overwritten.");
        if (State == PurgeProgressState.InterventionRequired)
            throw new InvalidOperationException("Intervention-required progress cannot be retried automatically.");
        if (Attempts >= 3)
            throw new InvalidOperationException("Fixture retry budget is exhausted.");
        LastReceiptId = receipt.ReceiptId;
        EvidenceHash = receipt.EvidenceHash;
        SafeFailureCode = receipt.SafeFailureCode;
        State = receipt.State;
        Attempts++;
        if (State is PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent)
            CompletedAt = observedAt;
        return true;
    }

    private static bool Sha256(string value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
