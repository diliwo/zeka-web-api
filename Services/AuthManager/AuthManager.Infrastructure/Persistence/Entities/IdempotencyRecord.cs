namespace AuthManager.Infrastructure.Persistence.Entities;

public enum IdempotencyStatus
{
    Processing = 0,
    Completed = 1
}

public sealed class IdempotencyRecord
{
    private IdempotencyRecord() { }

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public string KeyHash { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public IdempotencyStatus Status { get; private set; }
    public Guid? ResourceId { get; private set; }
    public string? SafeResponse { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public DateTimeOffset ProcessingExpiresAtUtc { get; private set; }
    public DateTimeOffset RetainUntilUtc { get; private set; }

    public static IdempotencyRecord Start(Guid userId, string operation, string keyHash,
        string requestHash, DateTimeOffset now, TimeSpan processingTimeout, TimeSpan retention) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Operation = operation,
        KeyHash = keyHash,
        RequestHash = requestHash,
        Status = IdempotencyStatus.Processing,
        CreatedAtUtc = now,
        UpdatedAtUtc = now,
        ProcessingExpiresAtUtc = now.Add(processingTimeout),
        RetainUntilUtc = now.Add(retention)
    };

    public void Complete(Guid resourceId, string safeResponse, DateTimeOffset now)
    {
        if (Status != IdempotencyStatus.Processing)
            throw new InvalidOperationException("Only a processing idempotency record can be completed.");

        ResourceId = resourceId;
        SafeResponse = safeResponse;
        Status = IdempotencyStatus.Completed;
        UpdatedAtUtc = now;
    }

    public void Restart(DateTimeOffset now, TimeSpan processingTimeout, TimeSpan retention)
    {
        if (Status != IdempotencyStatus.Processing || ProcessingExpiresAtUtc > now)
            throw new InvalidOperationException("Only an expired processing record can be restarted.");

        UpdatedAtUtc = now;
        ProcessingExpiresAtUtc = now.Add(processingTimeout);
        RetainUntilUtc = now.Add(retention);
    }
}
