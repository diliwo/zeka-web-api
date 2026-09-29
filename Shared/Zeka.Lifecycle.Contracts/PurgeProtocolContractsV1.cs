using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeka.Lifecycle.Contracts;

public enum PurgeCommandKindV1 { IrreversibleStarted = 1, ParticipantPurge = 2 }

public enum PurgeOutcomeV1
{
    Purged = 2,
    AlreadyAbsent = 3,
    FailedRetryable = 4,
    InterventionRequired = 5
}

/// <summary>
/// Provider-neutral LIFE-04A wire contract. The V1 fixture has exactly one synthetic
/// item per frozen owner category; this contract grants no production purge authority.
/// </summary>
public sealed record PurgeCommandV1(Guid MessageId, PurgeCommandKindV1 Kind,
    Guid OrganisationId, Guid TerminationOperationId, long OperationRevision,
    Guid RegistryRevision, Guid PlanId, string PlanHash, Guid DecisionSetId,
    string DecisionSetHash, string ParticipantId, string CapabilityKey,
    string Category, string ItemId, Guid IdempotencyId)
{
    public const string Version = "zeka-purge-message-v1";

    public static Guid StartId(Guid planId) => Identity("start", planId, "", "");
    public static Guid CommandId(Guid planId, string participantId, string category,
        string itemId) => Identity("participant", planId, participantId,
            category + "\n" + itemId);

    public string CanonicalJson() => JsonSerializer.Serialize(new object[]
    {
        Version, MessageId, (int)Kind, OrganisationId, TerminationOperationId,
        OperationRevision, RegistryRevision, PlanId, PlanHash, DecisionSetId,
        DecisionSetHash, ParticipantId, CapabilityKey, Category, ItemId, IdempotencyId
    });

    public string PayloadHash() => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(CanonicalJson()))).ToLowerInvariant();

    public static PurgeCommandV1 Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() != 16
            || document.RootElement[0].GetString() != Version)
            throw new InvalidOperationException("Unsupported purge command version or shape.");
        var fields = document.RootElement;
        var message = new PurgeCommandV1(fields[1].GetGuid(),
            (PurgeCommandKindV1)fields[2].GetInt32(), fields[3].GetGuid(), fields[4].GetGuid(),
            fields[5].GetInt64(), fields[6].GetGuid(), fields[7].GetGuid(),
            fields[8].GetString() ?? "", fields[9].GetGuid(), fields[10].GetString() ?? "",
            fields[11].GetString() ?? "", fields[12].GetString() ?? "",
            fields[13].GetString() ?? "", fields[14].GetString() ?? "", fields[15].GetGuid());
        if (message.CanonicalJson() != json || !Enum.IsDefined(message.Kind)
            || message.MessageId == Guid.Empty || message.OrganisationId == Guid.Empty
            || message.TerminationOperationId == Guid.Empty || message.OperationRevision < 1
            || message.RegistryRevision == Guid.Empty || message.PlanId == Guid.Empty
            || message.DecisionSetId == Guid.Empty || message.IdempotencyId != message.MessageId
            || !Sha256(message.PlanHash) || !Sha256(message.DecisionSetHash)
            || message.Kind == PurgeCommandKindV1.IrreversibleStarted
                && (message.ParticipantId != "" || message.Category != "" || message.ItemId != ""
                    || message.CapabilityKey != "organisation.purge-started"
                    || message.MessageId != StartId(message.PlanId))
            || message.Kind == PurgeCommandKindV1.ParticipantPurge
                && (string.IsNullOrWhiteSpace(message.ParticipantId)
                    || string.IsNullOrWhiteSpace(message.Category)
                    || string.IsNullOrWhiteSpace(message.ItemId)
                    || message.CapabilityKey != "organisation.disposition-category"
                    || message.MessageId != CommandId(message.PlanId,
                        message.ParticipantId, message.Category, message.ItemId)))
            throw new InvalidOperationException("Purge command canonical identity is invalid.");
        return message;
    }

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

    private static bool Sha256(string value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record PurgeParticipantReceiptV1(Guid ReceiptId, Guid CommandMessageId,
    Guid OrganisationId, Guid TerminationOperationId, long IrreversibleRevision,
    Guid RegistryRevision, Guid PlanId, string PlanHash, Guid DecisionSetId,
    string DecisionSetHash, string ParticipantId, string CapabilityKey, string Category,
    string ItemId, Guid IdempotencyId, PurgeOutcomeV1 State, string EvidenceHash,
    string? SafeFailureCode, int ContractVersion = 1);
