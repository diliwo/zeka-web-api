namespace AuthManager.Infrastructure.Persistence.Entities;

public enum OnboardingOperationKind { Register, Resend, Exchange }

public sealed class OnboardingOperationReceipt
{
    public Guid Id { get; init; }
    public OnboardingOperationKind Kind { get; init; }
    public Guid UserId { get; init; }
    public Guid ChallengeId { get; init; }
    public int Generation { get; init; }
    public Guid? OutboxId { get; init; }
    public Guid? GrantId { get; init; }
}
