using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication.Tests;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "72")]
public sealed class AssigneeEligibilityEndpointTests
{
    private const string Issuer = "https://synthetic-aks-issuer.example.invalid/cluster-a/";
    private const string Audience = "zeka-assignee-test-only";
    private const string Subject = "system:serviceaccount:zeka-test:clientmanagement-test";
    private const string Kid = "synthetic-key-a";

    [Theory]
    [InlineData("valid", HttpStatusCode.OK)]
    [InlineData("issuer", HttpStatusCode.Unauthorized)]
    [InlineData("audience", HttpStatusCode.Unauthorized)]
    [InlineData("subject", HttpStatusCode.Unauthorized)]
    [InlineData("algorithm", HttpStatusCode.Unauthorized)]
    [InlineData("signature", HttpStatusCode.Unauthorized)]
    [InlineData("kid", HttpStatusCode.Unauthorized)]
    [InlineData("expired", HttpStatusCode.Unauthorized)]
    [InlineData("overlong", HttpStatusCode.Unauthorized)]
    [InlineData("future_iat", HttpStatusCode.Unauthorized)]
    [InlineData("user_bearer", HttpStatusCode.Unauthorized)]
    public async Task Only_exact_short_lived_workload_credential_authenticates(string kind, HttpStatusCode expected)
    {
        await using var host = await TestHost.StartAsync();
        using var response = await host.PostAsync(host.Token(kind), ValidRequest());
        Assert.Equal(expected, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("synthetic-key", await response.Content.ReadAsStringAsync());
        Assert.Equal(expected == HttpStatusCode.OK ? 1 : 0, host.Resolver.Calls);
    }

    [Fact]
    public async Task Missing_or_disabled_service_identity_never_uses_user_bearer_or_cookie()
    {
        await using var disabled = await TestHost.StartAsync(enabled: false);
        Assert.Equal(HttpStatusCode.Unauthorized, (await disabled.PostAsync(null, ValidRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await disabled.PostAsync(disabled.Token("valid"), ValidRequest())).StatusCode);
        Assert.Equal(0, disabled.Resolver.Calls);
    }

    [Fact]
    public async Task V1_positive_negative_and_request_validation_are_minimal_and_non_disclosing()
    {
        await using var host = await TestHost.StartAsync();
        var request = ValidRequest();
        using var positive = await host.PostAsync(host.Token("valid"), request);
        Assert.Equal(HttpStatusCode.OK, positive.StatusCode);
        using var positiveJson = JsonDocument.Parse(await positive.Content.ReadAsStringAsync());
        var fields = positiveJson.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
        Assert.Equal(new[] { "actorMembershipId", "contractVersion", "decisionVersion", "eligible",
            "observedAtUtc", "organisationId", "targetMembershipId", "targetViewScope" }.Order(), fields);
        Assert.True(positiveJson.RootElement.GetProperty("eligible").GetBoolean());
        Assert.Equal("Assigned", positiveJson.RootElement.GetProperty("targetViewScope").GetString());

        host.Resolver.Decision = null;
        using var denied = await host.PostAsync(host.Token("valid"), request);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        using var deniedJson = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "contractVersion", "eligible" },
            deniedJson.RootElement.EnumerateObject().Select(p => p.Name).Order());
        Assert.False(deniedJson.RootElement.GetProperty("eligible").GetBoolean());

        var invalid = request with { ContractVersion = 2 };
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostAsync(host.Token("valid"), invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostRawAsync(host.Token("valid"), "{broken-json")).StatusCode);
        var extraField = JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace("}", ",\"beneficiaryId\":\"sensitive\"}", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.PostRawAsync(host.Token("valid"), extraField)).StatusCode);
        Assert.Equal(2, host.Resolver.Calls);
    }

    [Fact]
    public async Task Authority_timeout_is_bounded_503_without_a_second_or_cached_decision()
    {
        await using var host = await TestHost.StartAsync();
        host.Resolver.DelayUntilCancelled = true;
        var started = DateTimeOffset.UtcNow;
        using var response = await host.PostAsync(host.Token("valid"), ValidRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.InRange((DateTimeOffset.UtcNow - started).TotalSeconds, 1.8, 4);
        Assert.Equal(1, host.Resolver.Calls);
    }

    [Fact]
    public async Task Malformed_or_stale_local_decision_fails_closed_as_503()
    {
        await using var host = await TestHost.StartAsync();
        host.Resolver.Decision = new("Unknown", "synthetic-v1", DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await host.PostAsync(host.Token("valid"), ValidRequest())).StatusCode);
        host.Resolver.Decision = new("All", "synthetic-v1", DateTimeOffset.UtcNow.AddSeconds(-3));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            (await host.PostAsync(host.Token("valid"), ValidRequest())).StatusCode);
    }

    [Fact]
    public async Task Trusted_key_unavailable_before_initial_observation_is_503_not_a_negative_decision()
    {
        await using var host = await TestHost.StartAsync();
        host.Jwks.Unavailable = true;
        using var response = await host.PostAsync(host.Token("valid"), ValidRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, host.Resolver.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Authority_failure_is_non_disclosing_503(bool timeout)
    {
        await using var host = await TestHost.StartAsync();
        host.Resolver.Failure = timeout ? new TimeoutException("private authority detail")
            : new InvalidOperationException("private authority detail");
        using var response = await host.PostAsync(host.Token("valid"), ValidRequest());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private authority detail", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, host.Resolver.Calls);
    }

    [Theory]
    [InlineData("Audience", "zeka-user-only")]
    [InlineData("Issuer", "http://insecure.example.invalid/")]
    [InlineData("ClientManagementSubject", "system:serviceaccount:other:bad/name")]
    public void Service_trust_configuration_rejects_wrong_authority(string key, string value)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Audience"] = "zeka-user-only",
            ["AssigneeServiceAuthentication:Enabled"] = "true",
            ["AssigneeServiceAuthentication:Issuer"] = Issuer,
            ["AssigneeServiceAuthentication:Audience"] = Audience,
            ["AssigneeServiceAuthentication:JwksUri"] = Issuer + "keys",
            ["AssigneeServiceAuthentication:ClientManagementSubject"] = Subject
        };
        values["AssigneeServiceAuthentication:" + key] = value;
        Assert.Throws<InvalidOperationException>(() => AssigneeServiceOptions.Read(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()));
    }

    [Fact]
    public async Task Decision_telemetry_contains_only_static_service_identity_and_versioned_outcome()
    {
        await using var host = await TestHost.StartAsync();
        var request = ValidRequest();
        var token = host.Token("valid");
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(token, request)).StatusCode);
        var messages = host.Logs.Messages("AuthManager.AssigneeEligibility");
        Assert.Single(messages);
        Assert.Contains(Subject, messages[0]);
        Assert.Contains("synthetic-v1", messages[0]);
        Assert.DoesNotContain(request.ActorSubjectId.ToString(), messages[0]);
        Assert.DoesNotContain(request.TargetMembershipId.ToString(), messages[0]);
        Assert.DoesNotContain(request.OrganisationId.ToString(), messages[0]);
        Assert.DoesNotContain(token, messages[0]);
    }

    [Fact]
    public async Task Trusted_key_rotation_and_outage_fail_closed()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await host.PostAsync(host.Token("valid"), ValidRequest())).StatusCode);
        using var next = RSA.Create(2048);
        host.Jwks.Document = AuthenticationFixture.PublicJwks(next, "synthetic-key-b");
        host.Clock.Advance(TimeSpan.FromSeconds(6)); // shared JWKS trust bounds refreshes to one per five seconds
        host.Resolver.Decision = new("Assigned", "synthetic-v2", host.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.OK,
            (await host.PostAsync(host.Token("valid", next, "synthetic-key-b"), ValidRequest())).StatusCode);
        host.Jwks.Unavailable = true;
        host.Clock.Advance(TimeSpan.FromSeconds(11));
        host.Resolver.Decision = new("Assigned", "synthetic-v2", host.Clock.GetUtcNow());
        // The existing trust may use a verified, still non-stale key during a refresh outage.
        Assert.Equal(HttpStatusCode.OK,
            (await host.PostAsync(host.Token("valid", next, "synthetic-key-b"), ValidRequest())).StatusCode);
        host.Clock.Advance(TimeSpan.FromSeconds(601));
        using var unavailable = await host.PostAsync(host.Token("valid", next, "synthetic-key-b"), ValidRequest());
        Assert.True(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 503, received {unavailable.StatusCode}; JWKS calls: {host.Jwks.Calls}.");
        Assert.Equal(3, host.Resolver.Calls);
        Assert.True(host.Jwks.Calls >= 4);
    }

    private static AssigneeEligibilityRequest ValidRequest() =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient http;
        private readonly RSA key;
        public FakeDecision Resolver { get; }
        public MutableJwks Jwks { get; }
        public MutableClock Clock { get; }
        public RecordingLoggerProvider Logs { get; }

        private TestHost(WebApplication app, HttpClient http, RSA key, FakeDecision resolver,
            MutableJwks jwks, MutableClock clock, RecordingLoggerProvider logs)
        { this.app = app; this.http = http; this.key = key; Resolver = resolver; Jwks = jwks; Clock = clock; Logs = logs; }

        public static async Task<TestHost> StartAsync(bool enabled = true)
        {
            var key = RSA.Create(2048);
            var jwks = new MutableJwks(AuthenticationFixture.PublicJwks(key, Kid));
            var clock = new MutableClock();
            var resolver = new FakeDecision();
            var logs = new RecordingLoggerProvider();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AssigneeServiceAuthentication:Enabled"] = enabled.ToString(),
                ["AssigneeServiceAuthentication:Issuer"] = Issuer,
                ["AssigneeServiceAuthentication:Audience"] = Audience,
                ["AssigneeServiceAuthentication:JwksUri"] = Issuer + "keys",
                ["AssigneeServiceAuthentication:ClientManagementSubject"] = Subject,
                ["Authentication:Audience"] = "zeka-user-only"
            });
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddAssigneeServiceAuthentication(builder.Configuration);
            if (enabled)
                builder.Services.AddHttpClient<AssigneeJwksSource>()
                    .ConfigurePrimaryHttpMessageHandler(() => jwks);
            builder.Services.AddSingleton<IAssigneeEligibility>(resolver);
            var app = builder.Build();
            app.UseAuthentication(); app.UseAuthorization(); app.MapAssigneeEligibility();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(address) }, key, resolver, jwks, clock, logs);
        }

        public string Token(string kind, RSA? signingKey = null, string kid = Kid)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signing = signingKey ?? key;
            using var wrong = RSA.Create(2048);
            var credentials = new SigningCredentials(new RsaSecurityKey(kind == "signature" ? wrong : signing)
                { KeyId = kind == "kid" ? "unknown-kid" : kid },
                kind == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256);
            var payload = new JwtPayload
            {
                ["iss"] = kind == "issuer" ? "https://other-cluster.example.invalid/" : Issuer,
                ["aud"] = kind is "audience" or "user_bearer" ? "zeka-user-only" : Audience,
                ["sub"] = kind == "subject" ? "system:serviceaccount:zeka-test:wrong" : Subject,
                ["iat"] = kind == "overlong" ? now - 601 : kind == "future_iat" ? now + 31 : now,
                ["nbf"] = now - 1,
                ["exp"] = kind == "expired" ? now - 60 : now + 600
            };
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(credentials), payload));
        }

        public Task<HttpResponseMessage> PostAsync(string? token, AssigneeEligibilityRequest request) =>
            PostRawAsync(token, JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public async Task<HttpResponseMessage> PostRawAsync(string? token, string body)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/assignee-eligibility")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await http.SendAsync(message);
        }

        public async ValueTask DisposeAsync()
        { http.Dispose(); await app.DisposeAsync(); key.Dispose(); }
    }

    private sealed class FakeDecision : IAssigneeEligibility
    {
        public int Calls { get; private set; }
        public bool DelayUntilCancelled { get; set; }
        public Exception? Failure { get; set; }
        public AssigneeEligibilityDecision? Decision { get; set; } = new("Assigned", "synthetic-v1", DateTimeOffset.UtcNow);
        public async Task<AssigneeEligibilityDecision?> ResolveAsync(
            AssigneeEligibilityRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            if (DelayUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Decision;
        }
    }

    private sealed class MutableJwks(string document) : HttpMessageHandler
    {
        public string Document { get; set; } = document;
        public bool Unavailable { get; set; }
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Unavailable ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(Unavailable ? "" : Document) });
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Text)> messages = [];
        public string[] Messages(string category)
        {
            lock (messages) return messages.Where(m => m.Category == category).Select(m => m.Text).ToArray();
        }
        public ILogger CreateLogger(string categoryName) => new Recorder(categoryName, messages);
        public void Dispose() { }
        private sealed class Recorder(string category, List<(string Category, string Text)> messages) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages) messages.Add((category, formatter(state, exception)));
            }
        }
        private sealed class NullScope : IDisposable
        { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }
}
