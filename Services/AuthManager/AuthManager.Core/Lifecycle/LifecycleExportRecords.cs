using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

public sealed class LifecycleInboxReceipt : ITenantOwnedEntity
{
    private LifecycleInboxReceipt() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string MessageType { get; private set; } = "";
    public string PayloadSha256 { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }

    public static LifecycleInboxReceipt Create(Guid messageId, Guid operationId, Guid organisationId,
        string messageType, string payloadSha256, DateTimeOffset receivedAt) => new()
    {
        MessageId = Required(messageId, nameof(messageId)),
        OperationId = Required(operationId, nameof(operationId)),
        OrganisationId = Required(organisationId, nameof(organisationId)),
        MessageType = Stable(messageType, nameof(messageType), 300),
        PayloadSha256 = Sha256(payloadSha256, nameof(payloadSha256)),
        ReceivedAt = Utc(receivedAt, nameof(receivedAt))
    };

    internal static Guid Required(Guid value, string name) => value != Guid.Empty
        ? value : throw new ArgumentException("Identifier must not be empty.", name);
    internal static DateTimeOffset Utc(DateTimeOffset value, string name) =>
        value != default && value.Offset == TimeSpan.Zero ? value
            : throw new ArgumentException("Timestamp must be non-default UTC.", name);
    internal static string Stable(string value, string name, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= maximum ? value
            : throw new ArgumentException("Value must be a stable non-empty identifier.", name);
    internal static string Sha256(string value, string name) =>
        value?.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value : throw new ArgumentException("Value must be a lowercase SHA-256 hash.", name);
}

public sealed class LifecycleExportFenceReceipt : ITenantOwnedEntity
{
    private LifecycleExportFenceReceipt() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public int ContractVersion { get; private set; }
    public long OperationRevision { get; private set; }
    public string FenceToken { get; private set; } = "";
    public long FenceRevision { get; private set; }
    public DateTimeOffset EnteredAt { get; private set; }
    public string ReceiptHash { get; private set; } = "";
    public Guid MessageId { get; private set; }
    public Guid CausationId { get; private set; }
    public Guid CorrelationId { get; private set; }

    public static LifecycleExportFenceReceipt Create(Guid operationId, Guid organisationId,
        string participantId, int contractVersion, long operationRevision, string fenceToken,
        long fenceRevision, DateTimeOffset enteredAt, string receiptHash, Guid messageId,
        Guid causationId, Guid correlationId) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        ParticipantId = LifecycleInboxReceipt.Stable(participantId, nameof(participantId), 200),
        ContractVersion = contractVersion > 0 ? contractVersion : throw new ArgumentOutOfRangeException(nameof(contractVersion)),
        OperationRevision = operationRevision > 0 ? operationRevision : throw new ArgumentOutOfRangeException(nameof(operationRevision)),
        FenceToken = LifecycleInboxReceipt.Stable(fenceToken, nameof(fenceToken), 200),
        FenceRevision = fenceRevision > 0 ? fenceRevision : throw new ArgumentOutOfRangeException(nameof(fenceRevision)),
        EnteredAt = LifecycleInboxReceipt.Utc(enteredAt, nameof(enteredAt)),
        ReceiptHash = LifecycleInboxReceipt.Sha256(receiptHash, nameof(receiptHash)),
        MessageId = LifecycleInboxReceipt.Required(messageId, nameof(messageId)),
        CausationId = LifecycleInboxReceipt.Required(causationId, nameof(causationId)),
        CorrelationId = LifecycleInboxReceipt.Required(correlationId, nameof(correlationId))
    };
}

public sealed class LifecycleExportFragment : ITenantOwnedEntity
{
    private LifecycleExportFragment() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public int ContractVersion { get; private set; }
    public long OperationRevision { get; private set; }
    public DateTimeOffset SnapshotAt { get; private set; }
    public string FenceToken { get; private set; } = "";
    public string FragmentHash { get; private set; } = "";
    public string CategoriesJson { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }

    public static LifecycleExportFragment Create(Guid operationId, Guid organisationId, string participantId,
        int contractVersion, long operationRevision, DateTimeOffset snapshotAt, string fenceToken,
        string fragmentHash, string categoriesJson, DateTimeOffset receivedAt) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        ParticipantId = LifecycleInboxReceipt.Stable(participantId, nameof(participantId), 200),
        ContractVersion = contractVersion > 0 ? contractVersion : throw new ArgumentOutOfRangeException(nameof(contractVersion)),
        OperationRevision = operationRevision > 0 ? operationRevision : throw new ArgumentOutOfRangeException(nameof(operationRevision)),
        SnapshotAt = LifecycleInboxReceipt.Utc(snapshotAt, nameof(snapshotAt)),
        FenceToken = LifecycleInboxReceipt.Stable(fenceToken, nameof(fenceToken), 200),
        FragmentHash = LifecycleInboxReceipt.Sha256(fragmentHash, nameof(fragmentHash)),
        CategoriesJson = !string.IsNullOrWhiteSpace(categoriesJson) ? categoriesJson
            : throw new ArgumentException("Category inventory is required.", nameof(categoriesJson)),
        ReceivedAt = LifecycleInboxReceipt.Utc(receivedAt, nameof(receivedAt))
    };
}

public sealed class LifecycleExportPackage : ITenantOwnedEntity
{
    private LifecycleExportPackage() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ManifestSha256 { get; private set; } = "";
    public string PackageSha256 { get; private set; } = "";
    public string PackageReference { get; private set; } = "";
    public DateTimeOffset CreatedAt { get; private set; }

    public static LifecycleExportPackage Create(Guid operationId, Guid organisationId,
        string manifestSha256, string packageSha256, string packageReference, DateTimeOffset createdAt) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        ManifestSha256 = LifecycleInboxReceipt.Sha256(manifestSha256, nameof(manifestSha256)),
        PackageSha256 = LifecycleInboxReceipt.Sha256(packageSha256, nameof(packageSha256)),
        PackageReference = LifecycleInboxReceipt.Stable(packageReference, nameof(packageReference), 500),
        CreatedAt = LifecycleInboxReceipt.Utc(createdAt, nameof(createdAt))
    };
}

public sealed class LifecycleCoordinatorLease : ITenantOwnedEntity
{
    private LifecycleCoordinatorLease() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid LeaseId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public long Version { get; private set; }

    public static LifecycleCoordinatorLease Create(Guid operationId, Guid organisationId,
        Guid leaseId, DateTimeOffset expiresAt) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        LeaseId = LifecycleInboxReceipt.Required(leaseId, nameof(leaseId)),
        ExpiresAt = LifecycleInboxReceipt.Utc(expiresAt, nameof(expiresAt)),
        Version = 1
    };

    public bool TryAcquire(Guid leaseId, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        LifecycleInboxReceipt.Required(leaseId, nameof(leaseId));
        LifecycleInboxReceipt.Utc(now, nameof(now));
        LifecycleInboxReceipt.Utc(expiresAt, nameof(expiresAt));
        if (ExpiresAt > now && LeaseId != leaseId) return false;
        LeaseId = leaseId;
        ExpiresAt = expiresAt;
        Version++;
        return true;
    }
}

public enum AuthExportParticipantState
{
    FenceEntered,
    FragmentReady,
    Released
}

public sealed class AuthExportParticipantExecution : ITenantOwnedEntity
{
    private AuthExportParticipantExecution() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string FenceToken { get; private set; } = "";
    public long FenceRevision { get; private set; }
    public DateTimeOffset FenceEnteredAt { get; private set; }
    public string FenceReceiptHash { get; private set; } = "";
    public DateTimeOffset? SnapshotAt { get; private set; }
    public string? FenceEvidenceHash { get; private set; }
    public string? FragmentHash { get; private set; }
    public string? CategoriesJson { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }
    public AuthExportParticipantState State { get; private set; }

    public static AuthExportParticipantExecution Enter(Guid operationId, Guid organisationId,
        long operationRevision, string fenceToken, DateTimeOffset enteredAt, string receiptHash) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        OperationRevision = operationRevision > 0 ? operationRevision : throw new ArgumentOutOfRangeException(nameof(operationRevision)),
        FenceToken = LifecycleInboxReceipt.Stable(fenceToken, nameof(fenceToken), 200),
        FenceRevision = 1,
        FenceEnteredAt = LifecycleInboxReceipt.Utc(enteredAt, nameof(enteredAt)),
        FenceReceiptHash = LifecycleInboxReceipt.Sha256(receiptHash, nameof(receiptHash)),
        State = AuthExportParticipantState.FenceEntered
    };

    public void RecordFragment(DateTimeOffset snapshotAt, string fenceEvidenceHash,
        string fragmentHash, string categoriesJson)
    {
        if (State == AuthExportParticipantState.FragmentReady)
        {
            if (SnapshotAt != snapshotAt || FenceEvidenceHash != fenceEvidenceHash
                || FragmentHash != fragmentHash || CategoriesJson != categoriesJson)
                throw new InvalidOperationException("A ready Auth fragment is immutable.");
            return;
        }
        if (State != AuthExportParticipantState.FenceEntered)
            throw new InvalidOperationException("Auth export fence must be entered before staging.");
        SnapshotAt = LifecycleInboxReceipt.Utc(snapshotAt, nameof(snapshotAt));
        FenceEvidenceHash = LifecycleInboxReceipt.Sha256(fenceEvidenceHash, nameof(fenceEvidenceHash));
        FragmentHash = LifecycleInboxReceipt.Sha256(fragmentHash, nameof(fragmentHash));
        CategoriesJson = !string.IsNullOrWhiteSpace(categoriesJson) ? categoriesJson
            : throw new ArgumentException("Category evidence is required.", nameof(categoriesJson));
        State = AuthExportParticipantState.FragmentReady;
    }

    public void Release(DateTimeOffset releasedAt)
    {
        if (State == AuthExportParticipantState.Released) return;
        if (State is not (AuthExportParticipantState.FenceEntered or AuthExportParticipantState.FragmentReady))
            throw new InvalidOperationException("Auth export participant cannot be released from its current state.");
        ReleasedAt = LifecycleInboxReceipt.Utc(releasedAt, nameof(releasedAt));
        State = AuthExportParticipantState.Released;
    }
}

public sealed class AuthExportParticipantInboxReceipt : ITenantOwnedEntity
{
    private AuthExportParticipantInboxReceipt() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string MessageType { get; private set; } = "";
    public string PayloadSha256 { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }
    public static AuthExportParticipantInboxReceipt Create(Guid messageId, Guid operationId,
        Guid organisationId, string messageType, string payloadSha256, DateTimeOffset receivedAt) => new()
    {
        MessageId = LifecycleInboxReceipt.Required(messageId, nameof(messageId)),
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        MessageType = LifecycleInboxReceipt.Stable(messageType, nameof(messageType), 300),
        PayloadSha256 = LifecycleInboxReceipt.Sha256(payloadSha256, nameof(payloadSha256)),
        ReceivedAt = LifecycleInboxReceipt.Utc(receivedAt, nameof(receivedAt))
    };
}

public sealed class AuthExportParticipantOutboxMessage : ITenantOwnedEntity
{
    private AuthExportParticipantOutboxMessage() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string MessageType { get; private set; } = "";
    public string PayloadJson { get; private set; } = "";
    public string PayloadSha256 { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }
    public static AuthExportParticipantOutboxMessage Create(Guid messageId, Guid operationId,
        Guid organisationId, string messageType, string payloadJson, string payloadSha256,
        DateTimeOffset occurredAt) => new()
    {
        MessageId = LifecycleInboxReceipt.Required(messageId, nameof(messageId)),
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        MessageType = LifecycleInboxReceipt.Stable(messageType, nameof(messageType), 300),
        PayloadJson = !string.IsNullOrWhiteSpace(payloadJson) ? payloadJson
            : throw new ArgumentException("Outbox payload is required.", nameof(payloadJson)),
        PayloadSha256 = LifecycleInboxReceipt.Sha256(payloadSha256, nameof(payloadSha256)),
        OccurredAt = LifecycleInboxReceipt.Utc(occurredAt, nameof(occurredAt))
    };
}
