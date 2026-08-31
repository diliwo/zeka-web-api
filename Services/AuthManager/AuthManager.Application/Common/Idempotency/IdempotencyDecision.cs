namespace AuthManager.Application.Common.Idempotency;

public enum IdempotencyDecisionKind
{
    Started = 0,
    Replay = 1,
    Conflict = 2,
    Processing = 3
}

public sealed record IdempotencyDecision(
    IdempotencyDecisionKind Kind,
    Guid? ResourceId = null,
    string? SafeResponse = null);
