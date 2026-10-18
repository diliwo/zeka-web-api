using Microsoft.Extensions.Configuration;

namespace AuthManager.Infrastructure.Identity;

// Host-supplied policy only: no approved production durations or key are supplied by this slice.
public sealed class BrowserSessionPolicy
{
    public string HashKeyReference { get; init; } = "";
    public TimeSpan AccessLifetime { get; init; }
    public TimeSpan IdleLifetime { get; init; }
    public TimeSpan AbsoluteLifetime { get; init; }
    public bool IsValid => Path.IsPathFullyQualified(HashKeyReference)
        && AccessLifetime > TimeSpan.Zero && AccessLifetime <= TimeSpan.FromHours(1)
        && IdleLifetime >= AccessLifetime && AbsoluteLifetime >= IdleLifetime
        && AbsoluteLifetime <= TimeSpan.FromDays(365);
    public static BrowserSessionPolicy Read(IConfiguration configuration) =>
        configuration.GetSection("BrowserSession").Get<BrowserSessionPolicy>() ?? new();
    public override string ToString() => "Browser session policy (redacted)";
}
