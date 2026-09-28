namespace AdminAreaManagement.Infrastructure.Persistence.Exports;

public sealed class AdminAreaExportFence : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string FenceToken { get; private set; } = string.Empty;
    public long FenceRevision { get; private set; }
    public DateTimeOffset EnteredAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    private AdminAreaExportFence() { }

    public AdminAreaExportFence(Guid operationId, Guid organisationId, long operationRevision,
        string fenceToken, long fenceRevision, DateTimeOffset enteredAt)
    {
        if (operationId == Guid.Empty || organisationId == Guid.Empty || operationRevision <= 0
            || fenceRevision <= 0 || string.IsNullOrWhiteSpace(fenceToken) || enteredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("A complete UTC export fence identity is required.");
        OperationId = operationId;
        OrganisationId = organisationId;
        OperationRevision = operationRevision;
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

public sealed class AdminAreaExportInbox : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid MessageId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid OperationId { get; private set; }
    public string CommandType { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    private AdminAreaExportInbox() { }

    public AdminAreaExportInbox(Guid messageId, Guid organisationId, Guid operationId,
        string commandType, string requestHash, DateTimeOffset processedAt)
    {
        MessageId = messageId;
        OrganisationId = organisationId;
        OperationId = operationId;
        CommandType = commandType;
        RequestHash = requestHash;
        ProcessedAt = processedAt;
    }
}

public sealed class AdminAreaExportOutbox : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid MessageId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public Guid OperationId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }

    private AdminAreaExportOutbox() { }

    public AdminAreaExportOutbox(Guid messageId, Guid organisationId, Guid operationId,
        string messageType, string payloadJson, DateTimeOffset occurredAt)
    {
        MessageId = messageId;
        OrganisationId = organisationId;
        OperationId = operationId;
        MessageType = messageType;
        PayloadJson = payloadJson;
        OccurredAt = occurredAt;
    }
}

public sealed class AdminAreaExportFragment : Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity
{
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public DateTimeOffset SnapshotAt { get; private set; }
    public string FenceToken { get; private set; } = string.Empty;
    public string FragmentHash { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;

    private AdminAreaExportFragment() { }

    public AdminAreaExportFragment(Guid operationId, Guid organisationId, string participantId,
        DateTimeOffset snapshotAt, string fenceToken, string fragmentHash, string payloadJson)
    {
        OperationId = operationId;
        OrganisationId = organisationId;
        ParticipantId = participantId;
        SnapshotAt = snapshotAt;
        FenceToken = fenceToken;
        FragmentHash = fragmentHash;
        PayloadJson = payloadJson;
    }
}
