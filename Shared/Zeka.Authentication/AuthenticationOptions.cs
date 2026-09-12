using Microsoft.Extensions.Configuration;
namespace Zeka.Authentication;

public sealed record AuthenticationOptions
{
    public override string ToString() => "Authentication configuration (redacted).";
    public string Issuer { get; init; } = "";
    public string Audience { get; init; } = "";
    public string JwksUri { get; init; } = "";
    public TimeSpan ClockSkew { get; init; } = TimeSpan.MinValue;
    public TimeSpan JwksRefreshInterval { get; init; }
    public TimeSpan JwksMaximumStaleness { get; init; }
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    public static AuthenticationOptions Read(IConfiguration configuration)
    {
        try
        {
            var section = configuration.GetSection("Authentication");
            string[] allowed = [nameof(Issuer), nameof(Audience), nameof(JwksUri), nameof(ClockSkew),
                nameof(JwksRefreshInterval), nameof(JwksMaximumStaleness)];
            if (section.GetChildren().Any(child => !allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)
                || child.GetChildren().Any())) throw new InvalidOperationException();
            var result = section.Get<AuthenticationOptions>() ?? throw new InvalidOperationException();
            result.Validate();
            return result;
        }
        catch (Exception) { throw new InvalidOperationException("Invalid authentication configuration."); }
    }
    public void Validate()
    {
        if (!Https(Issuer) || !Https(JwksUri) || string.IsNullOrWhiteSpace(Audience) || Audience != Audience.Trim()
            || Audience.Length > 256 || Audience.Any(char.IsWhiteSpace) || Audience.Contains(',')
            || ClockSkew < TimeSpan.Zero || ClockSkew > TimeSpan.FromMinutes(5)
            || JwksRefreshInterval < TimeSpan.FromSeconds(5) || JwksRefreshInterval > TimeSpan.FromHours(1)
            || JwksMaximumStaleness < JwksRefreshInterval || JwksMaximumStaleness > TimeSpan.FromHours(24))
            throw new InvalidOperationException("Invalid authentication configuration.");
    }
    public static bool Https(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && value == value.Trim();
}
