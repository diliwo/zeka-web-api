namespace AuthManager.Application.Onboarding;

public sealed class RegistrationRequest
{
    public string? Email { get; init; }
    public string? Password { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
}

public sealed class ResendVerificationRequest
{
    public string? Email { get; init; }
}

public sealed class ExchangeVerificationRequest
{
    public string? Token { get; init; }
}

public enum RegistrationOutcome { Accepted, Invalid, Unavailable }

public sealed class VerificationExchange
{
    public required RegistrationOutcome Outcome { get; init; }
    public string? Credential { get; init; }
    public DateTimeOffset? ExpiresAtUtc { get; init; }
}

public interface IRegistrationService
{
    string? NormalizeAddress(string? address);
    Guid? ReadChallengeId(string? token);
    Task<RegistrationOutcome> RegisterAsync(RegistrationRequest input, CancellationToken cancellationToken);
    Task<RegistrationOutcome> ResendAsync(string email, CancellationToken cancellationToken);
    Task<VerificationExchange> ExchangeAsync(string token, CancellationToken cancellationToken);
}
