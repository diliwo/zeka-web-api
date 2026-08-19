namespace AuthManager.Application.Authentication.Models;

public sealed record UserRegistrationResult(
    bool Succeeded,
    IReadOnlyCollection<string> Errors)
{
    public static UserRegistrationResult Success { get; } = new(true, Array.Empty<string>());

    public static UserRegistrationResult Failure(IEnumerable<string> errors) =>
        new(false, errors.ToArray());
}
