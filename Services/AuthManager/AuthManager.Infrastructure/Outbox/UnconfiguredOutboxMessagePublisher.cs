using AuthManager.Application.Common.Outbox;

namespace AuthManager.Infrastructure.Outbox;

internal sealed class UnconfiguredOutboxMessagePublisher : IOutboxMessagePublisher
{
    public Task PublishAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Outbox dispatch is enabled but no transport-specific IOutboxMessagePublisher is configured.");
}
