namespace AuthManager.Infrastructure.Persistence.Entities;

public enum VerificationChallengeState { Pending, Superseded, Consumed, Revoked }

public sealed class EmailVerificationChallenge
{
    public Guid Id { get; init; }
    public Guid UserId { get; init; }
    public int Generation { get; init; }
    public required string KeyId { get; init; }
    public required string AddressBinding { get; init; }
    public required string StampBinding { get; init; }
    public required string TokenHash { get; set; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public VerificationChallengeState Status { get; private set; }
    public DateTimeOffset? TransitionedAtUtc { get; private set; }
    public Guid Version { get; private set; }

    public void Transition(VerificationChallengeState next, DateTimeOffset now, Guid version)
    {
        if (Status != VerificationChallengeState.Pending || next == VerificationChallengeState.Pending)
            throw new InvalidOperationException("VERIFICATION_CHALLENGE_TRANSITION_REJECTED");
        Status = next;
        TransitionedAtUtc = now;
        Version = version;
    }
}
