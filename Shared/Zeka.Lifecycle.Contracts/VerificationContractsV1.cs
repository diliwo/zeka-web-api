using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Zeka.Lifecycle.Contracts;

/// <summary>Read-only, owner-local synthetic LIFE-05A verification. This grants no production authority.</summary>
public sealed record VerifyPurgeCommandV1(Guid MessageId, Guid OrganisationId,
    Guid TerminationOperationId, long IrreversibleRevision, Guid RegistryRevision,
    string InventoryHash, Guid DecisionSetId, string DecisionSetHash, Guid PlanId,
    string PlanHash, string ParticipantId, string CapabilityKey, string Category,
    string ItemId, string ExpectedDisposition, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt)
{
    public const string Version = "zeka-verify-purge-v1";

    public string CanonicalJson() => JsonSerializer.Serialize(new object?[]
    {
        Version, MessageId, OrganisationId, TerminationOperationId, IrreversibleRevision,
        RegistryRevision, InventoryHash, DecisionSetId, DecisionSetHash, PlanId, PlanHash,
        ParticipantId, CapabilityKey, Category, ItemId, ExpectedDisposition, RequestedAt, ExpiresAt
    });

    public string Hash() => Sha(CanonicalJson());

    public void Validate()
    {
        if (MessageId == Guid.Empty || OrganisationId == Guid.Empty
            || TerminationOperationId == Guid.Empty || IrreversibleRevision < 1
            || RegistryRevision == Guid.Empty || DecisionSetId == Guid.Empty || PlanId == Guid.Empty
            || !IsHash(InventoryHash) || !IsHash(DecisionSetHash) || !IsHash(PlanHash)
            || string.IsNullOrWhiteSpace(ParticipantId) || string.IsNullOrWhiteSpace(Category)
            || string.IsNullOrWhiteSpace(ItemId)
            || ExpectedDisposition is not ("PURGE" or "RETAIN")
            || CapabilityKey != "organisation.disposition-category"
            || RequestedAt == default || RequestedAt.Offset != TimeSpan.Zero
            || ExpiresAt.Offset != TimeSpan.Zero || ExpiresAt <= RequestedAt)
            throw new InvalidOperationException("Verification command identity or freshness is invalid.");
    }

    public static VerifyPurgeCommandV1 Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement;
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 18
            || values[0].GetString() != Version)
            throw new InvalidOperationException("Unsupported verification command version or shape.");
        var command = new VerifyPurgeCommandV1(values[1].GetGuid(), values[2].GetGuid(),
            values[3].GetGuid(), values[4].GetInt64(), values[5].GetGuid(),
            values[6].GetString() ?? "", values[7].GetGuid(), values[8].GetString() ?? "",
            values[9].GetGuid(), values[10].GetString() ?? "", values[11].GetString() ?? "",
            values[12].GetString() ?? "", values[13].GetString() ?? "",
            values[14].GetString() ?? "", values[15].GetString() ?? "",
            values[16].GetDateTimeOffset(), values[17].GetDateTimeOffset());
        command.Validate();
        if (command.CanonicalJson() != json)
            throw new InvalidOperationException("Non-canonical verification command.");
        return command;
    }

    internal static bool IsHash(string? value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string Sha(string value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

/// <summary>Scoped counts only; no fixture payload or deleted personal data is copied into evidence.</summary>
public sealed record VerifyPurgeReceiptV1(Guid ReceiptId, Guid CommandMessageId,
    string CommandHash, Guid OrganisationId, Guid TerminationOperationId,
    long IrreversibleRevision, Guid RegistryRevision, string InventoryHash,
    Guid DecisionSetId, string DecisionSetHash, Guid PlanId, string PlanHash,
    string ParticipantId, string CapabilityKey, string Category, string ItemId,
    string VerifierVersion, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt,
    int EligibleResidualCount, int RetainedPresentCount, int RetainedExpectedCount,
    int? FileResidualCount, int? FileExpectedCount, bool PostconditionSatisfied, string EvidenceHash)
{
    public const string Version = "zeka-verify-receipt-v1";
    public const string ProofScope = "lifecycle-managed live synthetic fixture stores/files only; excludes historical backups, restored snapshots, unreviewed exports, caches, search, telemetry, logs, and unknown external stores";

    public string CanonicalJson() => JsonSerializer.Serialize(new object?[]
    {
        Version, ReceiptId, CommandMessageId, CommandHash, OrganisationId,
        TerminationOperationId, IrreversibleRevision, RegistryRevision, InventoryHash,
        DecisionSetId, DecisionSetHash, PlanId, PlanHash, ParticipantId, CapabilityKey,
        Category, ItemId, VerifierVersion, ObservedAt, ExpiresAt, EligibleResidualCount,
        RetainedPresentCount, RetainedExpectedCount, FileResidualCount, FileExpectedCount,
        PostconditionSatisfied, EvidenceHash, ProofScope
    });

    public string Hash() => VerifyPurgeCommandV1.Sha(CanonicalJson());

    public void ValidateAgainst(VerifyPurgeCommandV1 command, DateTimeOffset now)
    {
        command.Validate();
        if (ReceiptId == Guid.Empty || CommandMessageId != command.MessageId
            || CommandHash != command.Hash() || OrganisationId != command.OrganisationId
            || TerminationOperationId != command.TerminationOperationId
            || IrreversibleRevision != command.IrreversibleRevision
            || RegistryRevision != command.RegistryRevision || InventoryHash != command.InventoryHash
            || DecisionSetId != command.DecisionSetId || DecisionSetHash != command.DecisionSetHash
            || PlanId != command.PlanId || PlanHash != command.PlanHash
            || ParticipantId != command.ParticipantId || CapabilityKey != command.CapabilityKey
            || Category != command.Category || ItemId != command.ItemId
            || string.IsNullOrWhiteSpace(VerifierVersion) || VerifierVersion.Length > 100
            || ObservedAt.Offset != TimeSpan.Zero || ObservedAt < command.RequestedAt
            || ObservedAt > now || ExpiresAt != command.ExpiresAt || now >= ExpiresAt
            || EligibleResidualCount < 0 || RetainedPresentCount < 0
            || RetainedExpectedCount < 0 || FileResidualCount < 0 || FileExpectedCount < 0
            || (FileResidualCount is null) != (FileExpectedCount is null)
            || !VerifyPurgeCommandV1.IsHash(EvidenceHash)
            || PostconditionSatisfied != (EligibleResidualCount == 0
                && RetainedPresentCount == RetainedExpectedCount
                && FileResidualCount == FileExpectedCount))
            throw new InvalidOperationException("Verification receipt is wrong, stale or inconsistent.");
    }
}

/// <summary>Fixture-only terminal fact, atomically enqueued with the terminal projection.</summary>
public sealed record OrganisationVerifiedPurgedV1(Guid OrganisationId,
    Guid TerminationOperationId, long TerminalRevision, Guid PlanId, string PlanHash,
    string VerificationEvidenceHash, DateTimeOffset VerifiedAt,
    string ProofScope = VerifyPurgeReceiptV1.ProofScope);
