namespace AuthManager.Application.Authentication;

public enum BrowserSessionOutcome { Success, Unauthenticated, Unavailable }

// These values cross an in-process boundary only. Never log or serialize the refresh credential.
public sealed class BrowserSessionResult
{
    public BrowserSessionOutcome Outcome { get; init; }
    public Guid SubjectId { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshSecret { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }
    public DateTimeOffset CookieExpiresAtUtc { get; init; }
    public override string ToString() => $"Browser session outcome: {Outcome}";
}

public interface IBrowserSessions
{
    Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken);
    Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken);
    Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken);
    Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken);
}

public interface ISessionAccessTokenIssuer
{
    Task<string> IssueSessionAsync(Guid subjectId, Guid sessionId, TimeSpan lifetime, CancellationToken cancellationToken);
}
