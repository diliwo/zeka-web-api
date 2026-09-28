using ClientManagement.Core.Common;

namespace ClientManagement.Core.Lifecycle;

public sealed class OrganisationExportCommandReceipt : TenantOwnedEntity
{
    private OrganisationExportCommandReceipt() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public string ResponseType { get; private set; } = string.Empty;
    public string ResponseJson { get; private set; } = string.Empty;
    public Guid ResultMessageId { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }

    public static OrganisationExportCommandReceipt Complete(Guid organisationId, Guid messageId, Guid operationId,
        long operationRevision, string requestHash, string responseType, string responseJson,
        Guid resultMessageId, DateTimeOffset completedAt)
    {
        if (organisationId == Guid.Empty || messageId == Guid.Empty || operationId == Guid.Empty
            || operationRevision <= 0 || string.IsNullOrWhiteSpace(requestHash)
            || string.IsNullOrWhiteSpace(responseType) || string.IsNullOrWhiteSpace(responseJson)
            || resultMessageId == Guid.Empty || completedAt == default || completedAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Export command receipt identity is invalid.");
        var receipt = new OrganisationExportCommandReceipt
        {
            MessageId = messageId, OperationId = operationId, OperationRevision = operationRevision,
            RequestHash = requestHash, ResponseType = responseType, ResponseJson = responseJson,
            ResultMessageId = resultMessageId, CompletedAt = completedAt
        };
        receipt.AssignToOrganisation(organisationId);
        return receipt;
    }
}

public sealed class OrganisationExportOutboxMessage : TenantOwnedEntity
{
    private OrganisationExportOutboxMessage() { }
    public Guid MessageId { get; private set; }
    public Guid OperationId { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public string PayloadSha256 { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }

    public static OrganisationExportOutboxMessage Stage(Guid organisationId, Guid messageId, Guid operationId,
        string messageType, string payloadJson, string payloadSha256, DateTimeOffset occurredAt)
    {
        if (organisationId == Guid.Empty || messageId == Guid.Empty || operationId == Guid.Empty
            || string.IsNullOrWhiteSpace(messageType) || string.IsNullOrWhiteSpace(payloadJson)
            || string.IsNullOrWhiteSpace(payloadSha256) || occurredAt == default || occurredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Export outbox identity is invalid.");
        var message = new OrganisationExportOutboxMessage
        {
            MessageId = messageId, OperationId = operationId, MessageType = messageType,
            PayloadJson = payloadJson, PayloadSha256 = payloadSha256, OccurredAt = occurredAt
        };
        message.AssignToOrganisation(organisationId);
        return message;
    }
}
