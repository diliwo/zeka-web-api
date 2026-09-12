using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication;
namespace AuthManager.Infrastructure.Authentication;

public interface IAccessTokenIssuer
{
    Task<string> IssueAsync(string subject, CancellationToken cancellationToken = default);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
public interface IPublicJwksPublisher
{
    Task<PublishedJwks> PrepareAsync(CancellationToken cancellationToken = default);
    void ConfirmPublication(PublishedJwks publication);
}
public sealed record PublishedJwks(string Json, string Fingerprint);

public sealed class AccessTokenIssuer(ISigningKeyProvider provider, IssuerOptions options,
    AuthenticationOptions validation, TimeProvider clock) : IAccessTokenIssuer, IPublicJwksPublisher
{
    private string? publishedFingerprint;
    private static readonly Meter Metrics = new("Zeka.Authentication.Issuer");
    private static readonly Counter<long> Outcomes = Metrics.CreateCounter<long>("authentication.issuer.outcomes");
    private static readonly ActivitySource Traces = new("Zeka.Authentication.Issuer");
    private static string Fingerprint(RSA key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    public async Task<PublishedJwks> PrepareAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var key = await provider.OpenPrivateKeyAsync(options.ActiveSigningKeyReference, cancellationToken);
            EnsurePrivate(key);
            var keys = new List<object> { PublicKey(key, options.ActiveKeyId) };
            foreach (var retiring in options.RetiringKeys.Where(x => clock.GetUtcNow() < x.RemoveAfterUtc))
            {
                using var previous = await provider.OpenPublicKeyAsync(retiring.PublicKeyReference, cancellationToken);
                keys.Add(PublicKey(previous, retiring.KeyId));
            }
            return new PublishedJwks(JsonSerializer.Serialize(new { keys }), Fingerprint(key));
        }
        catch { Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "unavailable")); throw new AuthenticationAuthorityUnavailableException(); }
    }
    public void ConfirmPublication(PublishedJwks publication)
    {
        Interlocked.Exchange(ref publishedFingerprint, publication.Fingerprint);
        Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "published"));
    }
    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var key = await provider.OpenPrivateKeyAsync(options.ActiveSigningKeyReference, cancellationToken);
            EnsurePrivate(key);
            return Fingerprint(key) == Volatile.Read(ref publishedFingerprint);
        }
        catch { return false; }
    }
    public async Task<string> IssueAsync(string subject, CancellationToken cancellationToken = default)
    {
        using var trace = Traces.StartActivity("authentication.issue");
        var outcome = "unavailable";
        try
        {
            if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256) throw new ArgumentException("Invalid subject.");
            using var key = await provider.OpenPrivateKeyAsync(options.ActiveSigningKeyReference, cancellationToken);
            EnsurePrivate(key);
            if (Fingerprint(key) != Volatile.Read(ref publishedFingerprint)) throw new AuthenticationAuthorityUnavailableException();
            var now = clock.GetUtcNow().UtcDateTime;
            var descriptor = new SecurityTokenDescriptor
            {
                Issuer = validation.Issuer, Audience = validation.Audience,
                Subject = new ClaimsIdentity([new Claim("sub", subject)]),
                IssuedAt = now, NotBefore = now, Expires = now + options.AccessTokenLifetime,
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key)
                {
                    KeyId = options.ActiveKeyId,
                    // The provider owns this short-lived private handle; never cache a signer using it.
                    CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
                }, SecurityAlgorithms.RsaSha256)
            };
            var result = new JsonWebTokenHandler().CreateToken(descriptor);
            outcome = "issued";
            return result;
        }
        catch (ArgumentException) { outcome = "invalid_request"; throw; }
        catch { throw new AuthenticationAuthorityUnavailableException(); }
        finally { trace?.SetTag("outcome", outcome); Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome)); }
    }
    private static void EnsurePrivate(RSA key)
    { if (key.KeySize < 2048 || key.ExportParameters(true).D is null) throw new AuthenticationAuthorityUnavailableException(); }
    private static object PublicKey(RSA rsa, string kid)
    {
        if (rsa.KeySize < 2048) throw new AuthenticationAuthorityUnavailableException();
        var key = rsa.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid, n = Base64UrlEncoder.Encode(key.Modulus!), e = Base64UrlEncoder.Encode(key.Exponent!) };
    }
}
