using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

public sealed class LifecycleClosureFenceReceipt : ITenantOwnedEntity
{
    private LifecycleClosureFenceReceipt() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public int ContractVersion { get; private set; }
    public long OperationRevision { get; private set; }
    public string FenceToken { get; private set; } = "";
    public long FenceRevision { get; private set; }
    public DateTimeOffset BoundaryEstablishedAt { get; private set; }
    public string ReceiptHash { get; private set; } = "";
    public Guid MessageId { get; private set; }
    public Guid CausationId { get; private set; }
    public Guid CorrelationId { get; private set; }

    public static LifecycleClosureFenceReceipt Create(Guid operationId, Guid organisationId,
        string participantId, int contractVersion, long operationRevision, string fenceToken,
        long fenceRevision, DateTimeOffset boundaryEstablishedAt, string receiptHash,
        Guid messageId, Guid causationId, Guid correlationId) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        ParticipantId = LifecycleInboxReceipt.Stable(participantId, nameof(participantId), 200),
        ContractVersion = contractVersion > 0 ? contractVersion : throw new ArgumentOutOfRangeException(nameof(contractVersion)),
        OperationRevision = operationRevision > 0 ? operationRevision : throw new ArgumentOutOfRangeException(nameof(operationRevision)),
        FenceToken = LifecycleInboxReceipt.Stable(fenceToken, nameof(fenceToken), 200),
        FenceRevision = fenceRevision > 0 ? fenceRevision : throw new ArgumentOutOfRangeException(nameof(fenceRevision)),
        BoundaryEstablishedAt = LifecycleInboxReceipt.Utc(boundaryEstablishedAt, nameof(boundaryEstablishedAt)),
        ReceiptHash = LifecycleInboxReceipt.Sha256(receiptHash, nameof(receiptHash)),
        MessageId = LifecycleInboxReceipt.Required(messageId, nameof(messageId)),
        CausationId = LifecycleInboxReceipt.Required(causationId, nameof(causationId)),
        CorrelationId = LifecycleInboxReceipt.Required(correlationId, nameof(correlationId))
    };
}

public enum AuthClosureParticipantState
{
    FenceEntered = 1,
    Released = 2
}

public sealed class AuthClosureParticipantExecution : ITenantOwnedEntity
{
    private AuthClosureParticipantExecution() { }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string FenceToken { get; private set; } = "";
    public long FenceRevision { get; private set; }
    public DateTimeOffset BoundaryEstablishedAt { get; private set; }
    public string ReceiptHash { get; private set; } = "";
    public DateTimeOffset? ReleasedAt { get; private set; }
    public AuthClosureParticipantState State { get; private set; }

    public static AuthClosureParticipantExecution Enter(Guid operationId, Guid organisationId,
        long operationRevision, string fenceToken, DateTimeOffset boundaryEstablishedAt,
        string receiptHash) => new()
    {
        OperationId = LifecycleInboxReceipt.Required(operationId, nameof(operationId)),
        OrganisationId = LifecycleInboxReceipt.Required(organisationId, nameof(organisationId)),
        OperationRevision = operationRevision > 0 ? operationRevision : throw new ArgumentOutOfRangeException(nameof(operationRevision)),
        FenceToken = LifecycleInboxReceipt.Stable(fenceToken, nameof(fenceToken), 200),
        FenceRevision = 1,
        BoundaryEstablishedAt = LifecycleInboxReceipt.Utc(boundaryEstablishedAt, nameof(boundaryEstablishedAt)),
        ReceiptHash = LifecycleInboxReceipt.Sha256(receiptHash, nameof(receiptHash)),
        State = AuthClosureParticipantState.FenceEntered
    };

    public void Release(DateTimeOffset releasedAt)
    {
        if (State == AuthClosureParticipantState.Released) return;
        if (State != AuthClosureParticipantState.FenceEntered)
            throw new InvalidOperationException("Auth closure fence cannot be released from its current state.");
        ReleasedAt = LifecycleInboxReceipt.Utc(releasedAt, nameof(releasedAt));
        State = AuthClosureParticipantState.Released;
    }
}

public sealed class AuthClosureParticipantInboxReceipt : ITenantOwnedEntity
{
    private AuthClosureParticipantInboxReceipt() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string MessageType { get; private set; } = "";
    public string PayloadSha256 { get; private set; } = "";
    public DateTimeOffset ReceivedAt { get; private set; }

    public static AuthClosureParticipantInboxReceipt Create(Guid messageId, Guid operationId,
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

public sealed class AuthClosureParticipantOutboxMessage : ITenantOwnedEntity
{
    private AuthClosureParticipantOutboxMessage() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string MessageType { get; private set; } = "";
    public string PayloadJson { get; private set; } = "";
    public string PayloadSha256 { get; private set; } = "";
    public DateTimeOffset OccurredAt { get; private set; }

    public static AuthClosureParticipantOutboxMessage Create(Guid messageId, Guid operationId,
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
