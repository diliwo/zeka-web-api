using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.WebUtilities;

[assembly: InternalsVisibleTo("Infrastructure.IntegrationTests")]

namespace AuthManager.Infrastructure.Onboarding;

internal sealed class VerificationTokenService(RegistrationOptions options)
{
    public string ActiveKeyId => options.ActiveKeyId;

    internal readonly record struct TokenIdentity(string KeyId, Guid ChallengeId, int Generation);

    public static bool TryRead(string? token, out TokenIdentity identity)
    {
        identity = default;
        if (token is null || token.Length is < 83 or > 126) return false;
        var parts = token.Split('.');
        if (parts.Length != 5 || parts[0] != "v1"
            || !RegistrationOptions.SafeIdentifier(parts[1], 32)
            || !Guid.TryParseExact(parts[2], "N", out var challengeId) || challengeId == Guid.Empty
            || parts[2] != challengeId.ToString("N")
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation <= 0 || parts[3] != generation.ToString(CultureInfo.InvariantCulture)
            || !TrySecret(parts[4], out var mac))
            return false;
        CryptographicOperations.ZeroMemory(mac);
        identity = new(parts[1], challengeId, generation);
        return true;
    }

    public string? Create(EmailVerificationChallenge challenge)
    {
        if (!options.Keys.TryGetValue(challenge.KeyId, out var key)) return null;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        // BinaryWriter uses UTF-8 with length prefixes for strings and little-endian integers.
        writer.Write("zeka.email-verification.v1");
        writer.Write(options.Deployment);
        writer.Write(challenge.KeyId);
        writer.Write(challenge.Id.ToString("N"));
        writer.Write(challenge.UserId.ToString("N"));
        writer.Write(challenge.Generation);
        writer.Write(challenge.AddressBinding);
        writer.Write(challenge.StampBinding);
        writer.Write(challenge.ExpiresAtUtc.ToUnixTimeMilliseconds());
        writer.Flush();
        var signed = HMACSHA256.HashData(key, stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        return string.Join('.', "v1", challenge.KeyId, challenge.Id.ToString("N"),
            challenge.Generation.ToString(CultureInfo.InvariantCulture), WebEncoders.Base64UrlEncode(signed));
    }

    public bool Matches(EmailVerificationChallenge challenge, string supplied)
    {
        var expected = Create(challenge);
        if (expected is null) return false;
        var suppliedHash = TokenHash(supplied);
        return FixedEqual(TokenHash(expected), suppliedHash) && FixedEqual(challenge.TokenHash, suppliedHash);
    }

    public static string TokenHash(string value) => Hash("zeka.email-verification.token-hash.v1", value);
    public static string AddressBinding(string value) => Hash("zeka.email-verification.address.v1", value);
    public static string StampBinding(string value) => Hash("zeka.email-verification.stamp.v1", value);
    public static string GrantHash(string value) => Hash("zeka.organisation-onboarding.grant.v1", value);

    public static bool IsGrantCredential(string? value)
    {
        if (!TrySecret(value, out var bytes)) return false;
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    private static string Hash(string purpose, string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\0" + value)));

    internal static bool FixedEqual(string expected, string presented)
        => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(presented));

    private static bool TrySecret(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: 43 } || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;
        bytes = WebEncoders.Base64UrlDecode(value);
        if (bytes.Length == 32 && WebEncoders.Base64UrlEncode(bytes) == value) return true;
        CryptographicOperations.ZeroMemory(bytes);
        return false;
    }
}
