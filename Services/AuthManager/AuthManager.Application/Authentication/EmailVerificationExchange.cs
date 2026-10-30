namespace AuthManager.Application.Authentication;

public static class OnboardingGrantPurposes
{
    public const string CreateOrganisation = "organisation_onboarding";
}

// Secrets cross in-process boundaries only through explicit methods, never serializable properties.
public sealed class EmailVerificationChallenge(Guid subjectId, Guid challengeId, DateTimeOffset expiresAtUtc, string token)
{
    private readonly string secret = token;
    public Guid SubjectId { get; } = subjectId;
    public Guid ChallengeId { get; } = challengeId;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public string GetTokenForDelivery() => secret;
    public override string ToString() => "Email verification challenge (secret redacted).";
}

public sealed class RestrictedOnboardingGrant(Guid subjectId, Guid grantId, DateTimeOffset issuedAtUtc,
    DateTimeOffset expiresAtUtc, string credential)
{
    private readonly string secret = credential;
    public Guid SubjectId { get; } = subjectId;
    public Guid GrantId { get; } = grantId;
    public string Purpose => OnboardingGrantPurposes.CreateOrganisation;
    public DateTimeOffset IssuedAtUtc { get; } = issuedAtUtc;
    public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
    public string GetCredentialForExchange() => secret;
    public override string ToString() => "Restricted onboarding grant (secret redacted).";
}

public enum EmailVerificationOutcome { Success, Rejected }
public sealed record EmailVerificationExchangeResult(EmailVerificationOutcome Outcome, RestrictedOnboardingGrant? Grant)
{
    public override string ToString() => $"Email verification outcome: {Outcome}.";
}

public sealed class EmailVerificationExchangeException(string code) : Exception(code) { }

public interface IEmailVerificationExchange
{
    // All operations require the caller's Auth transaction and provider execution-strategy boundary.
    // No secret may be delivered/exposed until that caller commits. Any exception requires rollback.
    Task<EmailVerificationChallenge> PrepareAsync(Guid subjectId, CancellationToken cancellationToken = default);
    Task<EmailVerificationExchangeResult> VerifyAsync(Guid subjectId, string token, CancellationToken cancellationToken = default);
    // The organisation write and this one-use consumption must commit or roll back together.
    Task<bool> ConsumeRestrictedGrantAsync(Guid subjectId, Guid grantId, string credential, string purpose,
        CancellationToken cancellationToken = default);
}
