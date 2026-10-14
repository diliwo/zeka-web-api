using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NodaTime.TimeZones;

namespace AuthManager.Infrastructure.TimeZones;

public sealed record ConfigureTimeZoneCommand(
    string Environment, Guid OrganisationId, string TimeZoneId, long ExpectedConfigVersion,
    Guid ApprovalId, string ChangeReference)
{
    public string ArtifactSha256()
    {
        // Versioned, fixed-order UTF-8 artifact. There is no whitespace or JSON aliasing.
        var canonical = string.Join('\n', "ConfigureTimeZone.v1", Environment,
            OrganisationId.ToString("D"), TimeZoneId,
            ExpectedConfigVersion.ToString(CultureInfo.InvariantCulture),
            ApprovalId.ToString("D"), ChangeReference, "");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

public sealed record HumanTimeZoneReleaseProof(
    string ExecutorSubject, string ReviewerSubject, string ReleaseEvidenceId,
    string ReviewedArtifactSha256, string CorrelationId);

/// <summary>Injected keys are synthetic in tests. Production has no enrolled key source.</summary>
public sealed class ConfigureTimeZoneApprovalVerifier(
    IReadOnlyDictionary<string, SecurityKey> approvedKeys, TimeProvider clock)
{
    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<string?> VerifyAsync(
        string compactJws, ConfigureTimeZoneCommand command)
    {
        if (string.IsNullOrWhiteSpace(compactJws) || compactJws.Length > 8192
            || command.OrganisationId == Guid.Empty || command.ApprovalId == Guid.Empty
            || command.ExpectedConfigVersion < 0
            || string.IsNullOrWhiteSpace(command.Environment) || command.Environment.Length > 80
            || string.IsNullOrWhiteSpace(command.ChangeReference) || command.ChangeReference.Length > 200
            || command.Environment.Contains('\n') || command.Environment.Contains('\r')
            || command.ChangeReference.Contains('\n') || command.ChangeReference.Contains('\r')
            || !IsCanonicalIana(command.TimeZoneId)) return null;

        JsonWebToken token;
        try { token = Handler.ReadJsonWebToken(compactJws); }
        catch (Exception) { return null; }
        if (token.Alg != SecurityAlgorithms.RsaSha256 || string.IsNullOrWhiteSpace(token.Kid)
            || !approvedKeys.TryGetValue(token.Kid, out var key)) return null;

        var result = await Handler.ValidateTokenAsync(compactJws, new TokenValidationParameters
        {
            IssuerSigningKey = key, ValidateIssuerSigningKey = true,
            RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = false, ValidateAudience = false,
            RequireExpirationTime = true, ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) =>
            {
                var now = clock.GetUtcNow().UtcDateTime;
                return notBefore is not null && expires is not null
                    && notBefore <= now && expires > now
                    && expires - notBefore <= TimeSpan.FromMinutes(10);
            }
        });
        if (!result.IsValid || result.ClaimsIdentity is null) return null;
        var claims = result.ClaimsIdentity.Claims.GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Value).ToArray());
        string? One(string name) => claims.TryGetValue(name, out var values) && values.Length == 1
            ? values[0] : null;
        var owner = One("ownerSubject");
        if (One("purpose") != "ConfigureTimeZone"
            || One("environment") != command.Environment
            || One("organisationId") != command.OrganisationId.ToString("D")
            || One("timeZoneId") != command.TimeZoneId
            || One("expectedConfigVersion") != command.ExpectedConfigVersion.ToString(CultureInfo.InvariantCulture)
            || One("approvalId") != command.ApprovalId.ToString("D")
            || One("changeReference") != command.ChangeReference
            || One("artifactSha256") != command.ArtifactSha256()
            || string.IsNullOrWhiteSpace(owner) || owner.Length > 200)
            return null;
        return owner;
    }

    public static bool IsCanonicalIana(string? zone)
    {
        if (string.IsNullOrWhiteSpace(zone) || zone.Length > 128
            || zone != zone.Trim() || zone.Contains('\n') || zone.Contains('\r')) return false;
        var canonical = TzdbDateTimeZoneSource.Default.CanonicalIdMap;
        return canonical.TryGetValue(zone, out var resolved) && resolved == zone
            && !zone.StartsWith("Etc/GMT", StringComparison.Ordinal);
    }
}
