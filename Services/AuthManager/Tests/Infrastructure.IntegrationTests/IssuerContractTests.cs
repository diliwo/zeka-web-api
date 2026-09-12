using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.API.Endpoints;
using AuthManager.Infrastructure.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication;
using Zeka.Authentication.Tests;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class IssuerContractTests
{
    [Fact]
    public async Task One_issuer_token_is_accepted_by_all_three_actual_api_registrations()
    {
        using var fixture = new AuthenticationFixture();
        var issuer = Create(fixture);
        var publication = await issuer.PrepareAsync();
        issuer.ConfirmPublication(publication);
        var subject = Guid.NewGuid().ToString();
        var token = await issuer.IssueAsync(subject);
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("RS256", parsed.Header.Alg); Assert.Equal(AuthenticationFixture.KeyId, parsed.Header.Kid);
        Assert.Equal(AuthenticationFixture.Issuer, parsed.Issuer);
        Assert.Equal(AuthenticationFixture.Audience, Assert.Single(parsed.Audiences));
        Assert.Equal(subject, parsed.Subject);
        Action<IServiceCollection, IConfiguration>[] registrations =
        [
            (services, config) => AuthManager.API.TenantAuthentication.AddTenantAuthentication(services, config),
            (services, config) => AdminAreaManagement.API.TenantAuthentication.AddTenantAuthentication(services, config),
            (services, config) => ClientManagement.API.TenantAuthentication.AddTenantAuthentication(services, config)
        ];
        foreach (var register in registrations)
        {
            var services = new ServiceCollection().AddLogging();
            register(services, fixture.Configuration());
            services.AddSingleton<IJwksDocumentSource>(new DocumentSource(publication.Json));
            using var provider = services.BuildServiceProvider();
            Assert.Null(provider.GetService<ISigningKeyProvider>());
            Assert.Null(provider.GetService<IAccessTokenIssuer>());
            var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
            var result = await Assert.Single(options.TokenHandlers).ValidateTokenAsync(token, options.TokenValidationParameters);
            Assert.True(result.IsValid);
            Assert.Equal(subject, result.ClaimsIdentity.FindFirst("sub")!.Value);
            Assert.Null(options.TokenValidationParameters.IssuerSigningKey);
            Assert.Null(options.TokenValidationParameters.IssuerSigningKeys);
        }
    }

    [Fact]
    public async Task Issuer_refuses_unpublished_keys_and_key_replacement_requires_republication()
    {
        using var fixture = new AuthenticationFixture(); using var replacement = RSA.Create(2048);
        var provider = new TestKeys(fixture.Key);
        var issuer = Create(fixture, provider);
        Assert.False(await issuer.IsReadyAsync());
        await Assert.ThrowsAsync<AuthenticationAuthorityUnavailableException>(() => issuer.IssueAsync("subject"));
        var published = await issuer.PrepareAsync();
        Assert.False(await issuer.IsReadyAsync()); // preparing alone is not publication
        issuer.ConfirmPublication(published);
        Assert.True(await issuer.IsReadyAsync());
        await issuer.IssueAsync("subject");
        await issuer.IssueAsync("subject"); // fresh provider handle; no cached signer bound to a disposed key
        provider.Active = replacement.ExportParameters(true);
        Assert.False(await issuer.IsReadyAsync());
        await Assert.ThrowsAsync<AuthenticationAuthorityUnavailableException>(() => issuer.IssueAsync("subject"));
        issuer.ConfirmPublication(await issuer.PrepareAsync());
        Assert.True(await issuer.IsReadyAsync());
    }

    [Fact]
    public async Task Rotation_retains_previous_public_key_for_maximum_lifetime_and_skew_then_retires_it()
    {
        using var fixture = new AuthenticationFixture(); using var next = RSA.Create(2048);
        var settings = AuthenticationOptions.Read(fixture.Configuration());
        var now = fixture.Clock.GetUtcNow();
        var options = Options() with
        {
            ActiveKeyId = "next",
            RetiringKeys = [new() { KeyId = AuthenticationFixture.KeyId, PublicKeyReference = Reference("old-public"),
                LastIssuedAtUtc = now, RemoveAfterUtc = now + IssuerOptions.MaximumAccessTokenLifetime + settings.ClockSkew }]
        };
        options.Validate(settings);
        var provider = new TestKeys(next) { Previous = fixture.Key.ExportParameters(false) };
        var issuer = Create(fixture, provider, options);
        var publication = await issuer.PrepareAsync();
        using var json = JsonDocument.Parse(publication.Json);
        Assert.Equal(2, json.RootElement.GetProperty("keys").GetArrayLength());
        issuer.ConfirmPublication(publication);
        var source = new DocumentSource(publication.Json);
        var trust = new JwksTrust(source, settings, fixture.Clock);
        var services = new ServiceCollection().AddLogging();
        AuthManager.API.TenantAuthentication.AddTenantAuthentication(services, fixture.Configuration());
        services.AddSingleton<IJwksTrust>(trust);
        using var validators = services.BuildServiceProvider();
        var jwt = validators.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("Bearer");
        var handler = Assert.Single(jwt.TokenHandlers);
        var oldToken = fixture.Token();
        var newToken = await issuer.IssueAsync(Guid.NewGuid().ToString());
        Assert.True((await handler.ValidateTokenAsync(oldToken, jwt.TokenValidationParameters)).IsValid);
        Assert.True((await handler.ValidateTokenAsync(newToken, jwt.TokenValidationParameters)).IsValid);
        Assert.NotNull(await trust.ResolveAsync(AuthenticationFixture.KeyId));
        Assert.NotNull(await trust.ResolveAsync("next"));
        fixture.Clock.Advance(IssuerOptions.MaximumAccessTokenLifetime + settings.ClockSkew);
        source.Document = (await issuer.PrepareAsync()).Json;
        await Assert.ThrowsAsync<InvalidBearerTokenException>(() => trust.ResolveAsync(AuthenticationFixture.KeyId));
        Assert.NotNull(await trust.ResolveAsync("next"));
        Assert.Single(JsonDocument.Parse(source.Document).RootElement.GetProperty("keys").EnumerateArray());
        Assert.False((await handler.ValidateTokenAsync(oldToken, jwt.TokenValidationParameters)).IsValid);
        Assert.True((await handler.ValidateTokenAsync(newToken, jwt.TokenValidationParameters)).IsValid);
        var early = options with { RetiringKeys = [options.RetiringKeys[0] with { RemoveAfterUtc = now.AddMinutes(59) }] };
        Assert.Throws<InvalidOperationException>(() => early.Validate(settings));
    }

    [Fact]
    public async Task Host_supplied_private_file_is_read_only_and_issuance_uses_the_supplied_key()
    {
        using var fixture = new AuthenticationFixture();
        var reference = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(reference, fixture.Key.ExportPkcs8PrivateKeyPem());
            var original = await File.ReadAllBytesAsync(reference);
            var issuer = new AccessTokenIssuer(new FileSigningKeyProvider(),
                Options() with { ActiveSigningKeyReference = reference },
                AuthenticationOptions.Read(fixture.Configuration()), fixture.Clock);
            issuer.ConfirmPublication(await issuer.PrepareAsync());
            Assert.True(await issuer.IsReadyAsync());
            Assert.NotEmpty(await issuer.IssueAsync(Guid.NewGuid().ToString()));
            Assert.True(original.SequenceEqual(await File.ReadAllBytesAsync(reference)));
        }
        finally { File.Delete(reference); } // this test's own unique temporary key; never a host credential
    }

    [Fact]
    public async Task Missing_signing_material_fails_readiness_and_issuance_without_fallback()
    {
        using var fixture = new AuthenticationFixture();
        var issuer = new AccessTokenIssuer(new FileSigningKeyProvider(), Options(),
            AuthenticationOptions.Read(fixture.Configuration()), fixture.Clock);
        Assert.False(await issuer.IsReadyAsync());
        await Assert.ThrowsAsync<AuthenticationAuthorityUnavailableException>(() => issuer.IssueAsync("subject"));
        await Assert.ThrowsAsync<AuthenticationAuthorityUnavailableException>(() => issuer.PrepareAsync());
    }

    [Fact]
    public async Task Public_only_or_weak_signing_material_is_rejected()
    {
        using var fixture = new AuthenticationFixture(); using var weak = RSA.Create(1024);
        foreach (var parameters in new[] { fixture.Key.ExportParameters(false), weak.ExportParameters(true) })
        {
            var issuer = Create(fixture, new TestKeys(fixture.Key) { Active = parameters });
            Assert.False(await issuer.IsReadyAsync());
            await Assert.ThrowsAsync<AuthenticationAuthorityUnavailableException>(() => issuer.IssueAsync("subject"));
        }
    }

    [Fact]
    public async Task Actual_jwks_endpoint_exposes_only_public_fields_and_controls_readiness()
    {
        using var fixture = new AuthenticationFixture();
        var issuer = Create(fixture);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<IAccessTokenIssuer>(issuer).AddSingleton<IPublicJwksPublisher>(issuer);
        await using var app = builder.Build(); app.MapIssuerEndpoints(); await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/authentication")).StatusCode);
        using var response = await client.GetAsync("/.well-known/jwks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        foreach (var key in json.RootElement.GetProperty("keys").EnumerateArray())
            Assert.All(key.EnumerateObject(), field => Assert.Contains(field.Name, new[] { "kty", "kid", "use", "alg", "n", "e" }));
        Assert.DoesNotContain(Options().ActiveSigningKeyReference, body);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!await issuer.IsReadyAsync(timeout.Token)) await Task.Delay(10, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/authentication")).StatusCode);
        Assert.Equal("RS256", new JwtSecurityTokenHandler().ReadJwtToken(await issuer.IssueAsync("subject")).Header.Alg);
    }

    [Fact]
    public async Task Typed_issuer_registration_shares_one_publication_and_signing_boundary()
    {
        using var fixture = new AuthenticationFixture();
        var config = new ConfigurationBuilder().AddConfiguration(fixture.Configuration()).AddInMemoryCollection(
            new Dictionary<string, string?> {
                ["AuthenticationIssuer:ActiveSigningKeyReference"] = Options().ActiveSigningKeyReference,
                ["AuthenticationIssuer:ActiveKeyId"] = Options().ActiveKeyId,
                ["AuthenticationIssuer:AccessTokenLifetime"] = "00:05:00" }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<ISigningKeyProvider>(new TestKeys(fixture.Key));
        services.AddAccessTokenIssuer(config);
        using var provider = services.BuildServiceProvider();
        var issuer = provider.GetRequiredService<IAccessTokenIssuer>();
        var publisher = provider.GetRequiredService<IPublicJwksPublisher>();
        Assert.False(await issuer.IsReadyAsync());
        publisher.ConfirmPublication(await publisher.PrepareAsync());
        Assert.True(await issuer.IsReadyAsync());
        Assert.NotEmpty(await issuer.IssueAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void Issuer_configuration_is_required_bounded_redacted_and_forbidden_downstream()
    {
        using var fixture = new AuthenticationFixture();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAccessTokenIssuer(fixture.Configuration()));
        var settings = AuthenticationOptions.Read(fixture.Configuration());
        Assert.Throws<InvalidOperationException>(() => (Options() with { AccessTokenLifetime = TimeSpan.FromDays(1) }).Validate(settings));
        Assert.Throws<InvalidOperationException>(() => (Options() with { ActiveSigningKeyReference = "relative-key" }).Validate(settings));
        Assert.Throws<InvalidOperationException>(() => (Options() with { RetiringKeys =
            [new() { KeyId = Options().ActiveKeyId, PublicKeyReference = Reference("old"), LastIssuedAtUtc = DateTimeOffset.UtcNow,
                RemoveAfterUtc = DateTimeOffset.UtcNow.AddHours(2) }] }).Validate(settings));
        Assert.DoesNotContain(Reference("active"), Options().ToString());
        var config = new ConfigurationBuilder().AddConfiguration(fixture.Configuration()).AddInMemoryCollection(
            new Dictionary<string, string?> { ["AuthenticationIssuer:ActiveSigningKeyReference"] = Reference("active") }).Build();
        Assert.Throws<InvalidOperationException>(() => ClientManagement.API.TenantAuthentication.AddTenantAuthentication(new ServiceCollection(), config));
        Assert.Throws<InvalidOperationException>(() => AdminAreaManagement.API.TenantAuthentication.AddTenantAuthentication(new ServiceCollection(), config));
    }

    [Theory]
    [InlineData("AuthenticationIssuer:Unexpected")]
    [InlineData("AuthenticationIssuer:RetiringKeys:0:Unexpected")]
    [InlineData("AuthenticationIssuer:RetiringKeys:0:KeyId:Nested")]
    public void Unknown_or_nested_issuer_configuration_fails_closed(string key)
    {
        using var fixture = new AuthenticationFixture();
        var now = fixture.Clock.GetUtcNow();
        var values = new Dictionary<string, string?>
        {
            ["AuthenticationIssuer:ActiveSigningKeyReference"] = Reference("active"),
            ["AuthenticationIssuer:ActiveKeyId"] = "active",
            ["AuthenticationIssuer:AccessTokenLifetime"] = "00:05:00",
            ["AuthenticationIssuer:RetiringKeys:0:KeyId"] = "retiring",
            ["AuthenticationIssuer:RetiringKeys:0:PublicKeyReference"] = Reference("retiring"),
            ["AuthenticationIssuer:RetiringKeys:0:LastIssuedAtUtc"] = now.ToString("O"),
            ["AuthenticationIssuer:RetiringKeys:0:RemoveAfterUtc"] = now.AddHours(2).ToString("O")
        };
        var settings = AuthenticationOptions.Read(fixture.Configuration());
        var valid = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Assert.Single(IssuerOptions.Read(valid, settings).RetiringKeys);
        values[key] = "unexpected";
        var invalid = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var error = Assert.Throws<InvalidOperationException>(() => IssuerOptions.Read(invalid, settings));

        Assert.Equal("Invalid issuer configuration.", error.Message);
    }

    internal static AccessTokenIssuer Create(AuthenticationFixture fixture, TestKeys? provider = null, IssuerOptions? options = null)
        => new(provider ?? new TestKeys(fixture.Key), options ?? Options(), AuthenticationOptions.Read(fixture.Configuration()), fixture.Clock);
    private static string Reference(string name) => Path.Combine(Path.GetTempPath(), "not-provisioned-issue44-test", name);
    private static IssuerOptions Options() => new()
    { ActiveSigningKeyReference = Reference("active"), ActiveKeyId = AuthenticationFixture.KeyId, AccessTokenLifetime = TimeSpan.FromMinutes(5) };
    internal sealed class TestKeys(RSA key) : ISigningKeyProvider
    {
        public RSAParameters Active { get; set; } = key.ExportParameters(true);
        public RSAParameters Previous { get; set; }
        public Task<RSA> OpenPrivateKeyAsync(string reference, CancellationToken token)
        { var rsa = RSA.Create(); rsa.ImportParameters(Active); return Task.FromResult(rsa); }
        public Task<RSA> OpenPublicKeyAsync(string reference, CancellationToken token)
        { var rsa = RSA.Create(); rsa.ImportParameters(Previous); return Task.FromResult(rsa); }
    }
}
