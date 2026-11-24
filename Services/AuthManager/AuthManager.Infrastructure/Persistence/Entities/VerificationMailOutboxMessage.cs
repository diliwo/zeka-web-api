namespace AuthManager.Infrastructure.Persistence.Entities;

public enum VerificationMailState
{
    Pending, Leased, RetryScheduled, Delivered, Superseded, Expired, Consumed, Revoked, DeadLettered
}

public sealed class VerificationMailOutboxMessage
{
    public const int SupportedVersion = 1;
    public Guid Id { get; init; }
    public Guid OperationId { get; init; }
    public Guid ChallengeId { get; init; }
    public int Generation { get; init; }
    public int PayloadVersion { get; init; } = SupportedVersion;
    public DateTimeOffset EnqueuedAtUtc { get; init; }
    public VerificationMailState Status { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAtUtc { get; set; }
    public DateTimeOffset? DeliveredAtUtc { get; set; }
    public bool ObsoleteAtAcceptance { get; set; }
    public string? Outcome { get; set; }
    public Guid Version { get; set; }
}
