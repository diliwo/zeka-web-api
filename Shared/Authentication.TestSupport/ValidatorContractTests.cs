using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Xunit;
namespace Zeka.Authentication.Tests;

public abstract class ValidatorContractTests
{
    protected abstract void Register(IServiceCollection services, IConfiguration configuration);

    [Theory]
    [InlineData("hs256")] [InlineData("substitution")] [InlineData("none")] [InlineData("algorithm")]
    [InlineData("issuer")] [InlineData("audience")] [InlineData("signature")] [InlineData("expiry")]
    [InlineData("kid")] [InlineData("malformed")]
    public async Task Invalid_tokens_are_generic_401(string invalid)
    {
        using var fixture = new AuthenticationFixture();
        await using var host = await Host(fixture, new DocumentSource(fixture.Jwks()));
        var token = invalid == "malformed" ? "malformed-private-token" : fixture.Token(invalid, invalid == "kid" ? null : AuthenticationFixture.KeyId);
        using var result = await host.Send(token);
        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Equal("Bearer", result.Headers.WwwAuthenticate.Single().ToString());
        Assert.Empty(await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Empty_trusted_jwks_rejects_unknown_key_as_401_and_fails_readiness()
    {
        using var fixture = new AuthenticationFixture();
        await using var host = await Host(fixture, new DocumentSource("{\"keys\":[]}"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(fixture.Token())).StatusCode);
        Assert.Equal(HealthStatus.Unhealthy, (await host.App.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync()).Status);
    }

    [Theory]
    [InlineData("private")] [InlineData("duplicate")] [InlineData("malformed")]
    public async Task Unusable_jwks_never_establishes_trust(string invalid)
    {
        using var fixture = new AuthenticationFixture();
        using var parsed = System.Text.Json.JsonDocument.Parse(fixture.Jwks());
        var key = parsed.RootElement.GetProperty("keys")[0].GetRawText();
        var document = invalid == "malformed" ? "malformed-document"
            : invalid == "duplicate" ? "{\"keys\":[" + key + "," + key + "]}"
            : "{\"keys\":[" + key[..^1] + ",\"d\":\"invalid-private-parameter\"}]}";
        await using var host = await Host(fixture, new DocumentSource(document));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Send(fixture.Token())).StatusCode);
    }

    [Fact]
    public async Task Common_audience_rs256_is_accepted_and_cannot_grant_permissions_by_itself()
    {
        using var fixture = new AuthenticationFixture();
        await using var host = await Host(fixture, new DocumentSource(fixture.Jwks()));
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token())).StatusCode);
    }

    [Fact]
    public async Task Unknown_kid_refreshes_once_then_succeeds_or_returns_401_after_trusted_discovery()
    {
        using var fixture = new AuthenticationFixture();
        var source = new DocumentSource(fixture.Jwks());
        await using var host = await Host(fixture, source);
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token())).StatusCode);
        using var next = RSA.Create(2048);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        source.Document = AuthenticationFixture.PublicJwks(next, "next");
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token(kid: "next", key: next))).StatusCode);
        Assert.Equal(2, source.Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(fixture.Token(kid: "unknown"))).StatusCode);
        Assert.Equal(3, source.Calls);
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await host.Send(fixture.Token(kid: "other-" + i))).StatusCode);
        Assert.Equal(3, source.Calls); // cooldown bounds random-key refresh storms
    }

    [Fact]
    public async Task Outage_allows_only_non_stale_matching_cache_and_recovers()
    {
        using var fixture = new AuthenticationFixture();
        var source = new DocumentSource(fixture.Jwks()) { Unavailable = true };
        await using var host = await Host(fixture, source);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Send(fixture.Token())).StatusCode);
        var health = host.App.Services.GetRequiredService<HealthCheckService>();
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync()).Status);
        source.Unavailable = false; fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token())).StatusCode);
        source.Unavailable = true; fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token())).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Send(fixture.Token(kid: "new"))).StatusCode);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        using var unavailable = await host.Send(fixture.Token());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.Empty(await unavailable.Content.ReadAsStringAsync());
        source.Unavailable = false; fixture.Clock.Advance(TimeSpan.FromSeconds(6));
        Assert.Equal(HttpStatusCode.OK, (await host.Send(fixture.Token())).StatusCode);
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync()).Status);
    }

    [Fact]
    public async Task Discovery_timeout_is_bounded()
    {
        using var fixture = new AuthenticationFixture();
        await using var host = await Host(fixture, new HangingSource());
        var started = Stopwatch.StartNew();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.Send(fixture.Token())).StatusCode);
        Assert.InRange(started.Elapsed.TotalSeconds, 1, 4);
    }

    [Theory]
    [InlineData("Authentication:Issuer", "http://issuer.invalid")]
    [InlineData("Authentication:Audience", "one two")]
    [InlineData("Authentication:Audience", "")]
    [InlineData("Authentication:JwksUri", "http://keys.invalid")]
    [InlineData("Authentication:ClockSkew", "-00:00:01")]
    [InlineData("Authentication:JwksRefreshInterval", "00:00:00")]
    [InlineData("Authentication:JwksMaximumStaleness", "infinite")]
    [InlineData("Authentication:Audience:0", "duplicate")]
    [InlineData("Authentication:SigningKey", "forbidden-placeholder")]
    public void Invalid_or_legacy_configuration_fails_startup_without_echoing_values(string name, string value)
    {
        using var fixture = new AuthenticationFixture();
        var config = new ConfigurationBuilder().AddConfiguration(fixture.Configuration())
            .AddInMemoryCollection(new Dictionary<string, string?> { [name] = value }).Build();
        var exception = Assert.Throws<InvalidOperationException>(() => Register(new ServiceCollection(), config));
        Assert.Equal("Invalid authentication configuration.", exception.Message);
    }

    [Fact]
    public async Task Public_validation_key_cannot_sign_and_no_private_material_is_distributed()
    {
        using var fixture = new AuthenticationFixture();
        var settings = AuthenticationOptions.Read(fixture.Configuration());
        var trust = new JwksTrust(new DocumentSource(fixture.Jwks()), settings, fixture.Clock);
        var publicKey = Assert.IsType<RsaSecurityKey>(await trust.ResolveAsync(AuthenticationFixture.KeyId));
        Assert.Null(publicKey.Parameters.D);
        using var rsa = RSA.Create(); rsa.ImportParameters(publicKey.Parameters);
        Assert.ThrowsAny<CryptographicException>(() => rsa.SignData([1, 2, 3], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.DoesNotContain("PRIVATE", fixture.Jwks());
    }

    [Fact]
    public async Task Redacted_diagnostics_distinguish_invalid_and_unavailable()
    {
        using var fixture = new AuthenticationFixture();
        var source = new DocumentSource(fixture.Jwks());
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tags = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        { if (instrument.Meter.Name == JwksTokenHandler.DiagnosticsName) listener.EnableMeasurementEvents(instrument); };
        meter.SetMeasurementEventCallback<long>((instrument, measurement, values, state) =>
        { foreach (var value in values) { tags.Add(value.Key); outcomes.Add(value.Value!.ToString()!); } });
        meter.Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = name => name.Name == JwksTokenHandler.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { foreach (var tag in activity.TagObjects) tags.Add(tag.Key); }
        };
        ActivitySource.AddActivityListener(listener);
        var logs = new CapturedLogs();
        await using var host = await Host(fixture, source, logs);
        await host.Send(fixture.Token("signature", subject: "private-subject"));
        source.Unavailable = true; fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await host.Send(fixture.Token(subject: "private-subject"));
        Assert.Contains("invalid", outcomes); Assert.Contains("unavailable", outcomes);
        Assert.All(tags, tag => Assert.Equal("outcome", tag));
        Assert.DoesNotContain(logs.Messages, value => value.Contains("private-") || value.Contains(AuthenticationFixture.KeyId)
            || value.Contains(fixture.Key.ExportRSAPublicKeyPem()));
    }

    private async Task<RunningHost> Host(AuthenticationFixture fixture, IJwksDocumentSource source, ILoggerProvider? logs = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        if (logs is not null) builder.Logging.AddProvider(logs);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        Register(builder.Services, fixture.Configuration());
        builder.Services.AddSingleton(source);
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);
        var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/protected", () => "ok").RequireAuthorization();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new HttpClient { BaseAddress = new Uri(address) });
    }
    private sealed class RunningHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public WebApplication App => app;
        public Task<HttpResponseMessage> Send(string token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return client.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { client.Dispose(); await app.DisposeAsync(); }
    }
    private sealed class HangingSource : IJwksDocumentSource
    {
        public async Task<string> ReadAsync(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return ""; }
    }
    private sealed class CapturedLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Messages { get; } = new();
        public ILogger CreateLogger(string name) => new Capture(Messages);
        public void Dispose() { }
        private sealed class Capture(System.Collections.Concurrent.ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception) + exception?.ToString());
        }
    }
}
