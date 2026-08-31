namespace AuthManager.Application.Common.Idempotency;

public interface IIdempotencyStore
{
    Task<IdempotencyDecision> TryBeginAsync(
        Guid userId,
        string operation,
        string key,
        string canonicalRequest,
        TimeSpan processingTimeout,
        TimeSpan retention,
        CancellationToken cancellationToken = default);

    Task CompleteAsync(
        Guid userId,
        string operation,
        string key,
        Guid resourceId,
        string safeResponse,
        CancellationToken cancellationToken = default);
}
