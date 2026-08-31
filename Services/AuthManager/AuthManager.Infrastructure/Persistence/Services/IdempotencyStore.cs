using System.Security.Cryptography;
using System.Text;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Persistence.Services;

internal sealed class IdempotencyStore(AuthDbContext dbContext, TimeProvider timeProvider) : IIdempotencyStore
{
    public async Task<IdempotencyDecision> TryBeginAsync(Guid userId, string operation, string key,
        string canonicalRequest, TimeSpan processingTimeout, TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRequest);
        if (userId == Guid.Empty) throw new ArgumentException("A user identifier is required.", nameof(userId));
        if (processingTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(processingTimeout));
        if (retention <= processingTimeout) throw new ArgumentOutOfRangeException(nameof(retention));

        var keyHash = Hash(key);
        var requestHash = Hash(canonicalRequest);
        var now = timeProvider.GetUtcNow();
        var existing = await FindAsync(userId, operation, keyHash, cancellationToken);

        if (existing is not null)
            return await DecideAsync(existing, requestHash, now, processingTimeout, retention, cancellationToken);

        var newRecord = IdempotencyRecord.Start(
            userId, operation, keyHash, requestHash, now, processingTimeout, retention);
        dbContext.IdempotencyRecords.Add(newRecord);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new IdempotencyDecision(IdempotencyDecisionKind.Started);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(newRecord).State = EntityState.Detached;
            existing = await FindAsync(userId, operation, keyHash, cancellationToken)
                ?? throw new InvalidOperationException("The competing idempotency record could not be loaded.");
            return await DecideAsync(existing, requestHash, now, processingTimeout, retention, cancellationToken);
        }
    }

    public async Task CompleteAsync(Guid userId, string operation, string key, Guid resourceId,
        string safeResponse, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        SensitiveDataGuard.ValidateJson(safeResponse, nameof(safeResponse));

        var record = await FindAsync(userId, operation, Hash(key), cancellationToken)
            ?? throw new InvalidOperationException("The idempotency operation has not been started.");

        record.Complete(resourceId, safeResponse, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IdempotencyDecision> DecideAsync(IdempotencyRecord record, string requestHash,
        DateTimeOffset now, TimeSpan processingTimeout, TimeSpan retention, CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(record.RequestHash), Convert.FromHexString(requestHash)))
            return new IdempotencyDecision(IdempotencyDecisionKind.Conflict);

        if (record.Status == IdempotencyStatus.Completed)
            return new IdempotencyDecision(IdempotencyDecisionKind.Replay, record.ResourceId, record.SafeResponse);

        if (record.ProcessingExpiresAtUtc > now)
            return new IdempotencyDecision(IdempotencyDecisionKind.Processing);

        record.Restart(now, processingTimeout, retention);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new IdempotencyDecision(IdempotencyDecisionKind.Started);
    }

    private Task<IdempotencyRecord?> FindAsync(Guid userId, string operation, string keyHash,
        CancellationToken cancellationToken) => dbContext.IdempotencyRecords.SingleOrDefaultAsync(
            value => value.UserId == userId && value.Operation == operation && value.KeyHash == keyHash,
            cancellationToken);

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
