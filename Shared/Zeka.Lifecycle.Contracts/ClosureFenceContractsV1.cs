using System.Text.Json.Serialization;

namespace Zeka.Lifecycle.Contracts;

public enum OrganisationClosureBoundaryDispositionV1
{
    Unknown = 0,
    NotEstablished = 1
}

public sealed record CloseOrganisationParticipantV1
{
    public CloseOrganisationParticipantV1(LifecycleMessageHeaderV1 header, DateTimeOffset closingAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        ClosingAt = ContractGuard.Utc(closingAt, nameof(closingAt));
        PayloadHash = CanonicalLifecycleHash
            .ForHeader("zeka-lifecycle-close-organisation-participant-v1", Header)
            .Add(ClosingAt)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public DateTimeOffset ClosingAt { get; }
    public string PayloadHash { get; }
}

public sealed record ReleaseOrganisationClosureFenceV1
{
    public ReleaseOrganisationClosureFenceV1(LifecycleMessageHeaderV1 header, string fenceToken)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        PayloadHash = CanonicalLifecycleHash
            .ForHeader("zeka-lifecycle-release-organisation-closure-fence-v1", Header)
            .Add(FenceToken)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public string PayloadHash { get; }
}

public sealed record OrganisationClosureParticipantCompletedV1
{
    public OrganisationClosureParticipantCompletedV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset boundaryEstablishedAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        FenceRevision = ContractGuard.Positive(fenceRevision, nameof(fenceRevision));
        BoundaryEstablishedAt = ContractGuard.Utc(boundaryEstablishedAt, nameof(boundaryEstablishedAt));
        ReceiptHash = ComputeReceiptHash(Header, FenceToken, FenceRevision, BoundaryEstablishedAt);
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public long FenceRevision { get; }
    public DateTimeOffset BoundaryEstablishedAt { get; }
    public string ReceiptHash { get; }

    internal static string ComputeReceiptHash(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset boundaryEstablishedAt) =>
        CanonicalLifecycleHash.ForHeader("zeka-lifecycle-closure-participant-completed-v1", header)
            .Add(fenceToken)
            .Add(fenceRevision)
            .Add(boundaryEstablishedAt)
            .Finish();
}

public sealed record OrganisationClosureParticipantFailedV1
{
    public OrganisationClosureParticipantFailedV1(
        LifecycleMessageHeaderV1 header,
        OrganisationClosureParticipantPhaseV1 phase,
        string failureCode,
        bool retryable,
        DateTimeOffset failedAt,
        OrganisationClosureBoundaryDispositionV1 boundaryDisposition =
            OrganisationClosureBoundaryDispositionV1.Unknown)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown closure participant phase.");
        Phase = phase;
        FailureCode = ContractGuard.StableKey(failureCode, nameof(failureCode));
        Retryable = retryable;
        FailedAt = ContractGuard.Utc(failedAt, nameof(failedAt));
        if (!Enum.IsDefined(boundaryDisposition))
            throw new ArgumentOutOfRangeException(nameof(boundaryDisposition), boundaryDisposition,
                "Unknown closure boundary disposition.");
        BoundaryDisposition = boundaryDisposition;
        ReceiptHash = CanonicalLifecycleHash
            .ForHeader("zeka-lifecycle-closure-participant-failed-v1", Header)
            .Add((int)Phase)
            .Add(FailureCode)
            .Add(Retryable ? 1 : 0)
            .Add(FailedAt)
            .Add((int)BoundaryDisposition)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public OrganisationClosureParticipantPhaseV1 Phase { get; }
    public string FailureCode { get; }
    public bool Retryable { get; }
    public DateTimeOffset FailedAt { get; }
    public OrganisationClosureBoundaryDispositionV1 BoundaryDisposition { get; }
    public string ReceiptHash { get; }
}

public sealed record OrganisationClosureFenceReleasedV1
{
    public OrganisationClosureFenceReleasedV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        DateTimeOffset releasedAt)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        ReleasedAt = ContractGuard.Utc(releasedAt, nameof(releasedAt));
        ReceiptHash = CanonicalLifecycleHash
            .ForHeader("zeka-lifecycle-closure-fence-released-v1", Header)
            .Add(FenceToken)
            .Add(ReleasedAt)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public DateTimeOffset ReleasedAt { get; }
    public string ReceiptHash { get; }
}

public sealed record ClosureFenceParticipantRequirementV1
{
    public ClosureFenceParticipantRequirementV1(string participantId, int contractVersion)
    {
        ParticipantId = ContractGuard.StableKey(participantId, nameof(participantId));
        ContractVersion = ContractGuard.Version(contractVersion, nameof(contractVersion));
    }

    public string ParticipantId { get; }
    public int ContractVersion { get; }
}

public sealed record OrganisationClosureFenceReceiptV1
{
    public OrganisationClosureFenceReceiptV1(OrganisationClosureParticipantCompletedV1 completed) : this(
        completed?.Header ?? throw new ArgumentNullException(nameof(completed)),
        completed.FenceToken,
        completed.FenceRevision,
        completed.BoundaryEstablishedAt,
        completed.ReceiptHash)
    {
    }

    [JsonConstructor]
    public OrganisationClosureFenceReceiptV1(
        LifecycleMessageHeaderV1 header,
        string fenceToken,
        long fenceRevision,
        DateTimeOffset boundaryEstablishedAt,
        string receiptHash)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        FenceRevision = ContractGuard.Positive(fenceRevision, nameof(fenceRevision));
        BoundaryEstablishedAt = ContractGuard.Utc(boundaryEstablishedAt, nameof(boundaryEstablishedAt));
        ReceiptHash = ContractGuard.Sha256(receiptHash, nameof(receiptHash));
        var expected = OrganisationClosureParticipantCompletedV1.ComputeReceiptHash(
            Header, FenceToken, FenceRevision, BoundaryEstablishedAt);
        if (!string.Equals(ReceiptHash, expected, StringComparison.Ordinal))
            throw new ArgumentException("Closure receipt hash does not match its canonical fields.", nameof(receiptHash));
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public string FenceToken { get; }
    public long FenceRevision { get; }
    public DateTimeOffset BoundaryEstablishedAt { get; }
    public string ReceiptHash { get; }
    public Guid OperationId => Header.OperationId;
    public Guid OrganisationId => Header.OrganisationId;
    public long OperationRevision => Header.OperationRevision;
    public string ParticipantId => Header.ParticipantId;
    public int ContractVersion => Header.ContractVersion;
}

public sealed class CompleteClosureFenceEvidenceV1
{
    public CompleteClosureFenceEvidenceV1(
        Guid registryRevision,
        string inventoryHash,
        IEnumerable<ClosureFenceParticipantRequirementV1> requiredParticipants,
        IEnumerable<OrganisationClosureFenceReceiptV1> receipts)
    {
        RegistryRevision = ContractGuard.Required(registryRevision, nameof(registryRevision));
        InventoryHash = ContractGuard.Sha256(inventoryHash, nameof(inventoryHash));
        ArgumentNullException.ThrowIfNull(requiredParticipants);
        ArgumentNullException.ThrowIfNull(receipts);

        var requirements = requiredParticipants.OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();
        var orderedReceipts = receipts.OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();
        if (requirements.Length == 0)
            throw new ArgumentException("At least one required closure participant is required.", nameof(requiredParticipants));
        if (requirements.Select(x => x.ParticipantId).Distinct(StringComparer.Ordinal).Count() != requirements.Length)
            throw new ArgumentException("Required closure participant identities must be unique.", nameof(requiredParticipants));
        if (orderedReceipts.Select(x => x.ParticipantId).Distinct(StringComparer.Ordinal).Count() != orderedReceipts.Length)
            throw new ArgumentException("Closure receipt participant identities must be unique.", nameof(receipts));
        if (requirements.Length != orderedReceipts.Length)
            throw new ArgumentException("Every required closure participant must have exactly one receipt.", nameof(receipts));

        for (var index = 0; index < requirements.Length; index++)
        {
            var requirement = requirements[index];
            var receipt = orderedReceipts[index];
            if (requirement.ParticipantId != receipt.ParticipantId
                || requirement.ContractVersion != receipt.ContractVersion)
                throw new ArgumentException("Closure receipts must match the frozen participant inventory.", nameof(receipts));
            if (receipt.OperationId != orderedReceipts[0].OperationId
                || receipt.OrganisationId != orderedReceipts[0].OrganisationId
                || receipt.OperationRevision != orderedReceipts[0].OperationRevision)
                throw new ArgumentException("Closure receipts must bind to one operation, organisation and revision.", nameof(receipts));
        }

        RequiredParticipants = Array.AsReadOnly(requirements);
        Receipts = Array.AsReadOnly(orderedReceipts);
        EvidenceHash = ComputeEvidenceHash();
    }

    public Guid RegistryRevision { get; }
    public string InventoryHash { get; }
    public IReadOnlyList<ClosureFenceParticipantRequirementV1> RequiredParticipants { get; }
    public IReadOnlyList<OrganisationClosureFenceReceiptV1> Receipts { get; }
    public string EvidenceHash { get; }
    public Guid OperationId => Receipts[0].OperationId;
    public Guid OrganisationId => Receipts[0].OrganisationId;
    public long OperationRevision => Receipts[0].OperationRevision;
    public DateTimeOffset LastBoundaryEstablishedAt => Receipts.Max(x => x.BoundaryEstablishedAt);

    private string ComputeEvidenceHash()
    {
        var hash = new CanonicalLifecycleHash("zeka-lifecycle-complete-closure-fence-evidence-v1")
            .Add(RegistryRevision)
            .Add(InventoryHash)
            .Add(RequiredParticipants.Count);
        foreach (var requirement in RequiredParticipants)
            hash.Add(requirement.ParticipantId).Add(requirement.ContractVersion);
        hash.Add(Receipts.Count);
        foreach (var receipt in Receipts)
            hash.Add(receipt.OperationId)
                .Add(receipt.OrganisationId)
                .Add(receipt.OperationRevision)
                .Add(receipt.ParticipantId)
                .Add(receipt.ContractVersion)
                .Add(receipt.FenceToken)
                .Add(receipt.FenceRevision)
                .Add(receipt.BoundaryEstablishedAt)
                .Add(receipt.ReceiptHash);
        return hash.Finish();
    }
}

public sealed record OrganisationClosingV1
{
    public OrganisationClosingV1(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset closingAt,
        Guid registryRevision,
        string inventoryHash)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        ClosingAt = ContractGuard.Utc(closingAt, nameof(closingAt));
        RegistryRevision = ContractGuard.Required(registryRevision, nameof(registryRevision));
        InventoryHash = ContractGuard.Sha256(inventoryHash, nameof(inventoryHash));
        FactHash = CanonicalLifecycleHash.ForHeader("zeka-lifecycle-organisation-closing-v1", Header)
            .Add(ClosingAt)
            .Add(RegistryRevision)
            .Add(InventoryHash)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public DateTimeOffset ClosingAt { get; }
    public Guid RegistryRevision { get; }
    public string InventoryHash { get; }
    public string FactHash { get; }
}

public sealed record OrganisationArchivedV1
{
    public OrganisationArchivedV1(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset archivedAt,
        string fenceEvidenceHash)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        ArchivedAt = ContractGuard.Utc(archivedAt, nameof(archivedAt));
        FenceEvidenceHash = ContractGuard.Sha256(fenceEvidenceHash, nameof(fenceEvidenceHash));
        FactHash = CanonicalLifecycleHash.ForHeader("zeka-lifecycle-organisation-archived-v1", Header)
            .Add(ArchivedAt)
            .Add(FenceEvidenceHash)
            .Finish();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public DateTimeOffset ArchivedAt { get; }
    public string FenceEvidenceHash { get; }
    public string FactHash { get; }
}
