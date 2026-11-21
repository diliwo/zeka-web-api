namespace AuthManager.Infrastructure.Persistence.Entities;

public sealed class OnboardingGrant
{
    public const string OrganisationPurpose = "OrganisationOnboarding";
    public Guid Id { get; init; }
    public Guid ChallengeId { get; init; }
    public Guid UserId { get; init; }
    public string Purpose { get; init; } = OrganisationPurpose;
    public required string CredentialHash { get; init; }
    public required string StampBinding { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset? ConsumedAtUtc { get; init; }
    public DateTimeOffset? RevokedAtUtc { get; init; }
}
