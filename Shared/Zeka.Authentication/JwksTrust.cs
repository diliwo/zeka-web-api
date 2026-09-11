using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
namespace Zeka.Authentication;

public sealed class AuthenticationAuthorityUnavailableException() : Exception("Authentication authority unavailable.");
public sealed class InvalidBearerTokenException() : Exception("Invalid bearer token.");

public interface IJwksDocumentSource { Task<string> ReadAsync(CancellationToken cancellationToken); }
public sealed class HttpJwksDocumentSource(HttpClient http, AuthenticationOptions options) : IJwksDocumentSource
{
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        // One request; no implicit retry or redirects. Body size and the whole operation are bounded.
        using var response = await http.GetAsync(options.JwksUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidOperationException();
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > 65536) throw new InvalidOperationException();
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}

public interface IJwksTrust
{
    Task<SecurityKey> ResolveAsync(string kid, CancellationToken cancellationToken = default);
    Task<bool> IsReadyAsync(CancellationToken cancellationToken = default);
}
public sealed class JwksTrust(IJwksDocumentSource source, AuthenticationOptions options, TimeProvider clock) : IJwksTrust
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, SecurityKey> keys = new(StringComparer.Ordinal);
    private long? observed;
    private long? lastAttempt;
    private bool lastAttemptFailed;

    public async Task<bool> IsReadyAsync(CancellationToken cancellationToken = default)
    {
        try { await ResolveAsync(keys.Keys.FirstOrDefault() ?? "", cancellationToken); return true; }
        catch (InvalidBearerTokenException) { return keys.Count > 0; }
        catch (AuthenticationAuthorityUnavailableException) { return false; }
    }

    public async Task<SecurityKey> ResolveAsync(string kid, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(AuthenticationOptions.RequestTimeout);
        try
        {
            await gate.WaitAsync(budget.Token);
            try
            {
                var now = clock.GetTimestamp();
                var fresh = observed.HasValue && clock.GetElapsedTime(observed.Value, now) < options.JwksMaximumStaleness;
                var known = keys.TryGetValue(kid, out var key);
                var due = !observed.HasValue || clock.GetElapsedTime(observed.Value, now) >= options.JwksRefreshInterval;
                // Global single-flight and five-second cooldown also bound random-kid storms.
                var mayRefresh = !lastAttempt.HasValue || clock.GetElapsedTime(lastAttempt.Value, now) >= TimeSpan.FromSeconds(5);
                if ((!fresh || !known || due) && mayRefresh)
                {
                    lastAttempt = now;
                    try
                    {
                        var document = await source.ReadAsync(budget.Token);
                        var replacement = ParsePublicKeys(document);
                        keys = replacement; observed = clock.GetTimestamp(); lastAttemptFailed = false;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { lastAttemptFailed = true; }
                }
                now = clock.GetTimestamp();
                fresh = observed.HasValue && clock.GetElapsedTime(observed.Value, now) < options.JwksMaximumStaleness;
                if (fresh && keys.TryGetValue(kid, out key)) return key;
                if (!fresh || lastAttemptFailed) throw new AuthenticationAuthorityUnavailableException();
                throw new InvalidBearerTokenException();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AuthenticationAuthorityUnavailableException(); }
    }

    internal static Dictionary<string, SecurityKey> ParsePublicKeys(string document)
    {
        using var json = JsonDocument.Parse(document);
        if (json.RootElement.EnumerateObject().Count() != 1) throw new InvalidOperationException();
        var elements = json.RootElement.GetProperty("keys");
        if (elements.ValueKind != JsonValueKind.Array || elements.GetArrayLength() > 16)
            throw new InvalidOperationException();
        // Refuse duplicate fields, private parameters, and key-selected discovery locations.
        foreach (var item in elements.EnumerateArray())
        {
            string[] allowed = ["kty", "kid", "use", "alg", "n", "e", "key_ops"];
            var fields = item.EnumerateObject().ToArray();
            if (fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
                || fields.Any(x => !allowed.Contains(x.Name))) throw new InvalidOperationException();
        }
        var set = new JsonWebKeySet(document);
        var result = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        foreach (var jwk in set.Keys)
        {
            if (jwk.Kty != "RSA" || jwk.Alg != SecurityAlgorithms.RsaSha256 || jwk.Use != "sig"
                || string.IsNullOrWhiteSpace(jwk.Kid) || jwk.Kid.Length > 128
                || jwk.KeyOps.Any(op => op != "verify") || string.IsNullOrEmpty(jwk.N) || string.IsNullOrEmpty(jwk.E))
                throw new InvalidOperationException();
            var parameters = new RSAParameters { Modulus = Base64UrlEncoder.DecodeBytes(jwk.N), Exponent = Base64UrlEncoder.DecodeBytes(jwk.E) };
            using var rsa = RSA.Create(); rsa.ImportParameters(parameters);
            if (rsa.KeySize < 2048) throw new InvalidOperationException();
            if (!result.TryAdd(jwk.Kid, new RsaSecurityKey(parameters) { KeyId = jwk.Kid })) throw new InvalidOperationException();
        }
        return result;
    }
}
