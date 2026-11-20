using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;

namespace Infrastructure.IntegrationTests;

public sealed class VerificationTokenTests
{
    [Fact]
    public void Canonical_payload_matches_an_independently_computed_vector()
    {
        using var inputs = new TokenInputs();
        using var options = inputs.Options("v");
        var tokens = new VerificationTokenService(options);
        var row = Challenge("v");
        var token = tokens.Create(row)!;
        row.TokenHash = VerificationTokenService.TokenHash(token);

        Assert.True(token.EndsWith(".gN1MAnayUn_Hqo8MF1eTUkHodTvHlHr8V9MEIcJMr_8", StringComparison.Ordinal));
        Assert.True(VerificationTokenService.TryRead(token, out var header));
        Assert.Equal(row.Id, header.ChallengeId);
        Assert.Equal(row.Generation, header.Generation);
        Assert.True(tokens.Matches(row, token));
        Assert.False(VerificationTokenService.IsGrantCredential(token));
        Assert.False(VerificationTokenService.GrantHash(token) == row.TokenHash);
    }

    [Fact]
    public void Restart_and_overlapping_rotation_keep_the_original_generation_key()
    {
        using var input = new TokenInputs();
        using var old = input.Options("v");
        var initial = new VerificationTokenService(old);
        var row = Challenge("v");
        var generated = initial.Create(row)!;
        row.TokenHash = VerificationTokenService.TokenHash(generated);

        using var overlapped = input.Options("next");
        var restarted = new VerificationTokenService(overlapped);
        Assert.True(generated == restarted.Create(row));
        Assert.True(restarted.Matches(row, generated));
        Assert.True(initial.Create(Challenge("next")) is not null);
        Assert.True(generated != restarted.Create(Challenge("next")));

        using var retired = input.Options("next", retainOld: false);
        Assert.Null(new VerificationTokenService(retired).Create(row));
        Assert.False(new VerificationTokenService(retired).Matches(row, generated));
    }

    [Fact]
    public void Tampered_id_version_generation_address_stamp_expiry_or_deployment_cannot_validate()
    {
        using var input = new TokenInputs();
        using var options = input.Options("v");
        using var other = input.Options("v", deployment: "other-fixture");
        var tokens = new VerificationTokenService(options);
        var row = Challenge("v");
        var token = tokens.Create(row)!;
        row.TokenHash = VerificationTokenService.TokenHash(token);

        Assert.False(VerificationTokenService.TryRead("v2" + token[2..], out _));
        Assert.False(VerificationTokenService.TryRead(token.Replace(".7.", ".07.", StringComparison.Ordinal), out _));
        Assert.False(VerificationTokenService.TryRead(token + "=", out _));
        Assert.False(tokens.Matches(row, token.Replace(".7.", ".8.", StringComparison.Ordinal)));
        Assert.False(tokens.Matches(row, token.Replace("111111", "333333", StringComparison.Ordinal)));
        Assert.False(tokens.Matches(Challenge("v", address: 'C'), token));
        Assert.False(tokens.Matches(Challenge("v", stamp: 'D'), token));
        Assert.False(tokens.Matches(Challenge("v", expiryOffsetMs: 1), token));
        Assert.False(new VerificationTokenService(other).Matches(row, token));
    }

    [Fact]
    public void Unknown_active_key_or_production_mailpit_fails_closed()
    {
        using var input = new TokenInputs();
        Assert.Throws<InvalidOperationException>(() => input.Options("unknown"));
        Assert.Throws<InvalidOperationException>(() => input.Options("v", environment: "Production", mailpit: true));
    }

    private static EmailVerificationChallenge Challenge(string key, char address = 'A', char stamp = 'B', int expiryOffsetMs = 0)
        => new()
        {
            Id = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            UserId = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Generation = 7, KeyId = key, AddressBinding = new(address, 64), StampBinding = new(stamp, 64),
            TokenHash = string.Empty, CreatedAtUtc = DateTimeOffset.UnixEpoch,
            ExpiresAtUtc = DateTimeOffset.UnixEpoch.AddHours(24).AddMilliseconds(expiryOffsetMs)
        };

    private sealed class TokenInputs : IDisposable
    {
        private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("zeka-known-vector-");

        public RegistrationOptions Options(string keyId, bool retainOld = true, string deployment = "unit-fixture",
            string environment = "LocalStack", bool mailpit = false)
        {
            var old = Path.Combine(directory.FullName, "vector-old.bin");
            var next = Path.Combine(directory.FullName, "vector-next.bin");
            File.WriteAllBytes(old, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());
            File.WriteAllBytes(next, Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OnboardingRegistration:Enabled"] = "true",
                ["OnboardingRegistration:Deployment"] = deployment,
                ["OnboardingRegistration:ActiveKeyId"] = keyId,
                ["OnboardingRegistration:VerificationKeyFiles:next"] = next,
                ["OnboardingRegistration:VerificationMailpitEnabled"] = mailpit.ToString()
            });
            if (retainOld) builder.Configuration["OnboardingRegistration:VerificationKeyFiles:v"] = old;
            return RegistrationOptions.Read(builder.Configuration, builder.Environment);
        }

        public void Dispose()
        {
            File.Delete(Path.Combine(directory.FullName, "vector-old.bin"));
            File.Delete(Path.Combine(directory.FullName, "vector-next.bin"));
            directory.Delete();
        }
    }
}
