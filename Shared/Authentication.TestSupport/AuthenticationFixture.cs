using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Zeka.Authentication.Tests;

public sealed class AuthenticationFixture : IDisposable
{
    public RSA Key { get; } = RSA.Create(2048);
    public MutableClock Clock { get; } = new();
    public const string Issuer = "https://issuer.example.invalid";
    public const string Audience = "zeka-test-api";
    public const string KeyId = "test-active";
    public IConfiguration Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Authentication:Issuer"] = Issuer, ["Authentication:Audience"] = Audience,
        ["Authentication:JwksUri"] = Issuer + "/.well-known/jwks.json",
        ["Authentication:ClockSkew"] = "00:00:30",
        ["Authentication:JwksRefreshInterval"] = "00:00:10",
        ["Authentication:JwksMaximumStaleness"] = "00:01:00"
    }).Build();
    public string Jwks(string kid = KeyId) => PublicJwks(Key, kid);
    public static string PublicJwks(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return JsonSerializer.Serialize(new { keys = new[] { new { kty = "RSA", kid, alg = "RS256", use = "sig",
            n = Base64UrlEncoder.Encode(p.Modulus!), e = Base64UrlEncoder.Encode(p.Exponent!) } } });
    }
    public string Token(string invalid = "", string? kid = KeyId, RSA? key = null, string? subject = null)
    {
        using var wrong = RSA.Create(2048);
        SecurityKey signing = new RsaSecurityKey(invalid == "signature" ? wrong : key ?? Key) { KeyId = kid };
        var algorithm = invalid == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256;
        if (invalid is "hs256" or "substitution")
        {
            signing = new SymmetricSecurityKey(invalid == "substitution" ? Key.ExportSubjectPublicKeyInfo() : RandomNumberGenerator.GetBytes(64)) { KeyId = kid };
            algorithm = SecurityAlgorithms.HmacSha256;
        }
        var token = new JwtSecurityToken(
            invalid == "issuer" ? "https://untrusted.invalid" : Issuer,
            invalid == "audience" ? "other-api" : Audience,
            [new Claim("sub", subject ?? Guid.NewGuid().ToString())],
            notBefore: DateTime.UtcNow.AddHours(-2),
            expires: invalid == "expiry" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            signingCredentials: invalid == "none" ? null : new SigningCredentials(signing, algorithm));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    public void Dispose() => Key.Dispose();
}

public sealed class MutableClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => now.UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(TimeSpan duration) => now += duration;
}
public sealed class DocumentSource(string document) : IJwksDocumentSource
{
    public string Document { get; set; } = document;
    public bool Unavailable { get; set; }
    public int Calls { get; private set; }
    public Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Unavailable ? Task.FromException<string>(new HttpRequestException("private-discovery-detail")) : Task.FromResult(Document);
    }
}
