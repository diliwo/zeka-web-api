using Microsoft.Extensions.Configuration;
using Zeka.Authentication;
namespace AuthManager.Infrastructure.Authentication;

public sealed record RetiringVerificationKey
{
    public override string ToString() => "Retiring verification key configuration (redacted).";
    public string KeyId { get; init; } = "";
    public string PublicKeyReference { get; init; } = "";
    public DateTimeOffset LastIssuedAtUtc { get; init; }
    public DateTimeOffset RemoveAfterUtc { get; init; }
}
public sealed record IssuerOptions
{
    public override string ToString() => "Issuer configuration (redacted).";
    // All prior deployments obey this ceiling, including when a later configured lifetime is shorter.
    public static readonly TimeSpan MaximumAccessTokenLifetime = TimeSpan.FromHours(1);
    public string ActiveSigningKeyReference { get; init; } = "";
    public string ActiveKeyId { get; init; } = "";
    public TimeSpan AccessTokenLifetime { get; init; }
    public RetiringVerificationKey[] RetiringKeys { get; init; } = [];
    public static IssuerOptions Read(IConfiguration configuration, AuthenticationOptions validation)
    {
        try
        {
            var section = configuration.GetSection("AuthenticationIssuer");
            string[] allowed = [nameof(ActiveSigningKeyReference), nameof(ActiveKeyId), nameof(AccessTokenLifetime), nameof(RetiringKeys)];
            if (section.GetChildren().Any(child => !allowed.Contains(child.Key, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException();
            foreach (var item in section.GetSection(nameof(RetiringKeys)).GetChildren())
            {
                string[] keyFields = ["KeyId", "PublicKeyReference", "LastIssuedAtUtc", "RemoveAfterUtc"];
                if (item.GetChildren().Any(child => !keyFields.Contains(child.Key, StringComparer.OrdinalIgnoreCase)
                    || child.GetChildren().Any())) throw new InvalidOperationException();
            }
            var options = section.Get<IssuerOptions>() ?? throw new InvalidOperationException();
            options.Validate(validation); return options;
        }
        catch (Exception) { throw new InvalidOperationException("Invalid issuer configuration."); }
    }
    public void Validate(AuthenticationOptions validation)
    {
        if (string.IsNullOrWhiteSpace(ActiveKeyId) || ActiveKeyId.Length > 128
            || !Path.IsPathFullyQualified(ActiveSigningKeyReference)
            || AccessTokenLifetime <= TimeSpan.Zero || AccessTokenLifetime > MaximumAccessTokenLifetime
            || RetiringKeys.Length > 15 || RetiringKeys.Select(x => x.KeyId).Append(ActiveKeyId).Distinct(StringComparer.Ordinal).Count() != RetiringKeys.Length + 1
            || RetiringKeys.Any(x => string.IsNullOrWhiteSpace(x.KeyId) || x.KeyId.Length > 128
                || !Path.IsPathFullyQualified(x.PublicKeyReference) || x.LastIssuedAtUtc == default
                || x.RemoveAfterUtc < x.LastIssuedAtUtc + MaximumAccessTokenLifetime + validation.ClockSkew
                || x.RemoveAfterUtc - x.LastIssuedAtUtc > TimeSpan.FromDays(1)))
            throw new InvalidOperationException("Invalid issuer configuration.");
    }
}
