namespace AdminAreaManagement.Infrastructure.Persistence.Closures;

public sealed class AdminAreaClosureFence : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public int ContractVersion { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public string FenceToken { get; private set; } = string.Empty;
    public long FenceRevision { get; private set; }
    public DateTimeOffset EnteredAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    private AdminAreaClosureFence() { }

    public AdminAreaClosureFence(Guid operationId, Guid organisationId, long operationRevision,
        string participantId, int contractVersion, string requestHash, string fenceToken,
        long fenceRevision, DateTimeOffset enteredAt)
    {
        if (operationId == Guid.Empty || organisationId == Guid.Empty || operationRevision <= 0
            || contractVersion <= 0 || string.IsNullOrWhiteSpace(participantId)
            || string.IsNullOrWhiteSpace(requestHash) || string.IsNullOrWhiteSpace(fenceToken)
            || fenceRevision <= 0
            || enteredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A complete UTC closure-fence identity is required.");
        OperationId = operationId;
        OrganisationId = organisationId;
        OperationRevision = operationRevision;
        ParticipantId = participantId;
        ContractVersion = contractVersion;
        RequestHash = requestHash;
        FenceToken = fenceToken;
        FenceRevision = fenceRevision;
        EnteredAt = enteredAt;
    }

    public void Release(DateTimeOffset releasedAt)
    {
        if (releasedAt.Offset != TimeSpan.Zero || releasedAt < EnteredAt)
            throw new ArgumentException("A valid UTC release time is required.", nameof(releasedAt));
        ReleasedAt ??= releasedAt;
    }
}

public sealed class AdminAreaClosureInbox : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid MessageId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid OperationId { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public string CommandType { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    private AdminAreaClosureInbox() { }

    public AdminAreaClosureInbox(Guid messageId, Guid organisationId, Guid operationId,
        string participantId, string commandType, string requestHash, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        OrganisationId = organisationId;
        OperationId = operationId;
        ParticipantId = participantId;
        CommandType = commandType;
        RequestHash = requestHash;
        ProcessedAt = processedAt;
    }
}

public sealed class AdminAreaClosureOutbox : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid MessageId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid OperationId { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public string MessageType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }

    private AdminAreaClosureOutbox() { }

    public AdminAreaClosureOutbox(Guid messageId, Guid organisationId, Guid operationId,
        string participantId, string messageType, string payloadJson, DateTimeOffset occurredAt)
    {
        MessageId = messageId;
        OrganisationId = organisationId;
        OperationId = operationId;
        ParticipantId = participantId;
        MessageType = messageType;
        PayloadJson = payloadJson;
        OccurredAt = occurredAt;
    }
}
