namespace AuthManager.Application.Common.Outbox;

public sealed record OutboxMessageRequest(
    string MessageType,
    int ContractVersion,
    string Payload,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? OrganisationId = null);

public sealed record OutboxMessageEnvelope(
    Guid Id,
    string MessageType,
    int ContractVersion,
    string Payload,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? OrganisationId);

public interface IOutboxWriter
{
    Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default);
}

public interface IOutboxMessagePublisher
{
    Task PublishAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken);
}

public interface IOutboxDispatcher
{
    Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default);
}
