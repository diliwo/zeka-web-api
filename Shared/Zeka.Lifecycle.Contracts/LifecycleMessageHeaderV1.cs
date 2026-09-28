namespace Zeka.Lifecycle.Contracts;

public sealed record LifecycleMessageHeaderV1
{
    public LifecycleMessageHeaderV1(
        Guid operationId,
        Guid organisationId,
        long operationRevision,
        string participantId,
        int contractVersion,
        Guid messageId,
        Guid causationId,
        Guid correlationId)
    {
        OperationId = ContractGuard.Required(operationId, nameof(operationId));
        OrganisationId = ContractGuard.Required(organisationId, nameof(organisationId));
        OperationRevision = ContractGuard.Positive(operationRevision, nameof(operationRevision));
        ParticipantId = ContractGuard.StableKey(participantId, nameof(participantId));
        ContractVersion = ContractGuard.Version(contractVersion, nameof(contractVersion));
        MessageId = ContractGuard.Required(messageId, nameof(messageId));
        CausationId = ContractGuard.Required(causationId, nameof(causationId));
        CorrelationId = ContractGuard.Required(correlationId, nameof(correlationId));
    }

    public Guid OperationId { get; }
    public Guid OrganisationId { get; }
    public long OperationRevision { get; }
    public string ParticipantId { get; }
    public int ContractVersion { get; }
    public Guid MessageId { get; }
    public Guid CausationId { get; }
    public Guid CorrelationId { get; }
}
