using AuthManager.Application.Common.Outbox;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Security;

namespace AuthManager.Infrastructure.Persistence.Services;

internal sealed class OutboxWriter(AuthDbContext dbContext, TimeProvider timeProvider) : IOutboxWriter
{
    public async Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        if (request.ContractVersion < 1) throw new ArgumentOutOfRangeException(nameof(request.ContractVersion));
        SensitiveDataGuard.ValidateJson(request.Payload, nameof(request));

        var message = OutboxMessage.Create(request.MessageType, request.ContractVersion, request.Payload,
            request.OccurredAtUtc.ToUniversalTime(), timeProvider.GetUtcNow(), request.CorrelationId, request.OrganisationId);
        dbContext.OutboxMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        return message.Id;
    }
}
