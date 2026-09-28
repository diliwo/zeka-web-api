using System.Text.Json.Serialization;

namespace Zeka.Lifecycle.Contracts;

public sealed record EnterOrganisationExportFenceV1
{
    public EnterOrganisationExportFenceV1(LifecycleMessageHeaderV1 header) =>
        Header = header ?? throw new ArgumentNullException(nameof(header));

    public LifecycleMessageHeaderV1 Header { get; }
}

public sealed record OrganisationExportFenceEnteredV1
{
    public OrganisationExportFenceEnteredV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset enteredAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        FenceRevision = ContractGuard.Positive(fenceRevision, nameof(fenceRevision));
        EnteredAt = ContractGuard.Utc(enteredAt, nameof(enteredAt));
        ReceiptHash = ComputeReceiptHash(Header, FenceToken, FenceRevision, EnteredAt);
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public long FenceRevision { get; }
    public DateTimeOffset EnteredAt { get; }
    public string ReceiptHash { get; }

    internal static string ComputeReceiptHash(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset enteredAt) =>
        CanonicalLifecycleHash.ForHeader("zeka-lifecycle-export-fence-receipt-v1", header)
            .Add(fenceToken).Add(fenceRevision).Add(enteredAt).Finish();
}

public sealed record ExportFenceParticipantRequirementV1
{
    public ExportFenceParticipantRequirementV1(string participantId, int contractVersion)
    {
        ParticipantId = ContractGuard.StableKey(participantId, nameof(participantId));
        ContractVersion = ContractGuard.Version(contractVersion, nameof(contractVersion));
    }

    public string ParticipantId { get; }
    public int ContractVersion { get; }
}

public sealed record ExportFenceReceiptV1
{
    public ExportFenceReceiptV1(OrganisationExportFenceEnteredV1 entered) : this(
        entered?.Header ?? throw new ArgumentNullException(nameof(entered)),
        entered.FenceToken,
        entered.FenceRevision,
        entered.EnteredAt,
        entered.ReceiptHash)
    {
    }

    [JsonConstructor]
    public ExportFenceReceiptV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset enteredAt,
        string receiptHash)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        FenceRevision = ContractGuard.Positive(fenceRevision, nameof(fenceRevision));
        EnteredAt = ContractGuard.Utc(enteredAt, nameof(enteredAt));
        ReceiptHash = ContractGuard.Sha256(receiptHash, nameof(receiptHash));
        var expectedHash = OrganisationExportFenceEnteredV1.ComputeReceiptHash(
            Header, FenceToken, FenceRevision, EnteredAt);
        if (!string.Equals(ReceiptHash, expectedHash, StringComparison.Ordinal))
            throw new ArgumentException("Fence receipt hash does not match its canonical fields.", nameof(receiptHash));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public long FenceRevision { get; }
    public DateTimeOffset EnteredAt { get; }
    public string ReceiptHash { get; }
    public Guid OperationId => Header.OperationId;
    public Guid OrganisationId => Header.OrganisationId;
    public long OperationRevision => Header.OperationRevision;
    public string ParticipantId => Header.ParticipantId;
    public int ContractVersion => Header.ContractVersion;
}

public sealed class CompleteExportFenceEvidenceV1
{
    public CompleteExportFenceEvidenceV1(
        Guid registryRevision,
        string inventoryHash,
        IEnumerable<ExportFenceParticipantRequirementV1> requiredParticipants,
        IEnumerable<ExportFenceReceiptV1> receipts)
    {
        RegistryRevision = ContractGuard.Required(registryRevision, nameof(registryRevision));
        InventoryHash = ContractGuard.Sha256(inventoryHash, nameof(inventoryHash));
        ArgumentNullException.ThrowIfNull(requiredParticipants);
        ArgumentNullException.ThrowIfNull(receipts);

        var requirements = requiredParticipants.OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();
        var orderedReceipts = receipts.OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();
        if (requirements.Length == 0)
            throw new ArgumentException("At least one required participant is required.", nameof(requiredParticipants));
        if (requirements.Select(x => x.ParticipantId).Distinct(StringComparer.Ordinal).Count() != requirements.Length)
            throw new ArgumentException("Required participant identities must be unique.", nameof(requiredParticipants));
        if (orderedReceipts.Select(x => x.ParticipantId).Distinct(StringComparer.Ordinal).Count() != orderedReceipts.Length)
            throw new ArgumentException("Fence receipt participant identities must be unique.", nameof(receipts));
        if (requirements.Length != orderedReceipts.Length)
            throw new ArgumentException("Every required participant must have exactly one fence receipt.", nameof(receipts));

        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            var receipt = orderedReceipts[index];
            if (requirement.ParticipantId != receipt.ParticipantId
                || requirement.ContractVersion != receipt.ContractVersion)
                throw new ArgumentException("Fence receipts must exactly match the required participant inventory.", nameof(receipts));
            if (receipt.OperationId != orderedReceipts[0].OperationId
                || receipt.OrganisationId != orderedReceipts[0].OrganisationId
                || receipt.OperationRevision != orderedReceipts[0].OperationRevision)
                throw new ArgumentException("Fence receipts must bind to one operation, organisation and revision.", nameof(receipts));
        }

        RequiredParticipants = Array.AsReadOnly(requirements);
        Receipts = Array.AsReadOnly(orderedReceipts);
        EvidenceHash = ComputeEvidenceHash();
    }

    public Guid RegistryRevision { get; }
    public string InventoryHash { get; }
    public IReadOnlyList<ExportFenceParticipantRequirementV1> RequiredParticipants { get; }
    public IReadOnlyList<ExportFenceReceiptV1> Receipts { get; }
    public string EvidenceHash { get; }
    public Guid OperationId => Receipts[0].OperationId;
    public Guid OrganisationId => Receipts[0].OrganisationId;
    public long OperationRevision => Receipts[0].OperationRevision;
    public DateTimeOffset LastFenceEnteredAt => Receipts.Max(x => x.EnteredAt);

    private string ComputeEvidenceHash()
    {
        var hash = new CanonicalLifecycleHash("zeka-lifecycle-complete-export-fence-evidence-v1")
            .Add(RegistryRevision).Add(InventoryHash).Add(RequiredParticipants.Count);
        foreach (var requirement in RequiredParticipants)
            hash.Add(requirement.ParticipantId).Add(requirement.ContractVersion);
        hash.Add(Receipts.Count);
        foreach (var receipt in Receipts)
            hash.Add(receipt.OperationId).Add(receipt.OrganisationId).Add(receipt.OperationRevision)
                .Add(receipt.ParticipantId).Add(receipt.ContractVersion).Add(receipt.FenceToken)
                .Add(receipt.FenceRevision).Add(receipt.EnteredAt).Add(receipt.ReceiptHash);
        return hash.Finish();
    }
}

public sealed record StageOrganisationExportV1
{
    public StageOrganisationExportV1(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset snapshotAt,
        CompleteExportFenceEvidenceV1 fenceEvidence)
        : this(header, snapshotAt, fenceEvidence, header?.ParticipantId
            ?? throw new ArgumentNullException(nameof(header)))
    {
    }

    public StageOrganisationExportV1(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset snapshotAt,
        CompleteExportFenceEvidenceV1 fenceEvidence,
        string fenceOwnerParticipantId)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        SnapshotAt = ContractGuard.Utc(snapshotAt, nameof(snapshotAt));
        FenceEvidence = fenceEvidence ?? throw new ArgumentNullException(nameof(fenceEvidence));
        FenceOwnerParticipantId = ContractGuard.StableKey(
            fenceOwnerParticipantId,
            nameof(fenceOwnerParticipantId));
        if (Header.OperationId != FenceEvidence.OperationId
            || Header.OrganisationId != FenceEvidence.OrganisationId
            || Header.OperationRevision != FenceEvidence.OperationRevision)
            throw new ArgumentException("Stage header must match the complete fence evidence.", nameof(header));
        if (!FenceEvidence.RequiredParticipants.Any(x =>
                x.ParticipantId == FenceOwnerParticipantId && x.ContractVersion == Header.ContractVersion))
            throw new ArgumentException(
                "Stage fence owner must belong to the frozen required fence inventory.",
                nameof(fenceOwnerParticipantId));
        if (SnapshotAt < FenceEvidence.LastFenceEnteredAt)
            throw new ArgumentException("SnapshotAt must be recorded after every required fence receipt.", nameof(snapshotAt));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public DateTimeOffset SnapshotAt { get; }
    public CompleteExportFenceEvidenceV1 FenceEvidence { get; }
    public string FenceOwnerParticipantId { get; }
    public string FenceEvidenceHash => FenceEvidence.EvidenceHash;
}

public sealed record ReleaseOrganisationExportFenceV1
{
    public ReleaseOrganisationExportFenceV1(LifecycleMessageHeaderV1 header, string fenceToken)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
}

public sealed record OrganisationExportFenceReleasedV1
{
    public OrganisationExportFenceReleasedV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        DateTimeOffset releasedAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        ReleasedAt = ContractGuard.Utc(releasedAt, nameof(releasedAt));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public DateTimeOffset ReleasedAt { get; }
}

public sealed record OrganisationExportParticipantFailedV1
{
    public OrganisationExportParticipantFailedV1(
        LifecycleMessageHeaderV1 header,
        OrganisationExportParticipantPhaseV1 phase,
        string failureCode,
        bool retryable,
        DateTimeOffset failedAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown export participant phase.");
        Phase = phase;
        FailureCode = ContractGuard.StableKey(failureCode, nameof(failureCode));
        Retryable = retryable;
        FailedAt = ContractGuard.Utc(failedAt, nameof(failedAt));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public OrganisationExportParticipantPhaseV1 Phase { get; }
    public string FailureCode { get; }
    public bool Retryable { get; }
    public DateTimeOffset FailedAt { get; }
}
