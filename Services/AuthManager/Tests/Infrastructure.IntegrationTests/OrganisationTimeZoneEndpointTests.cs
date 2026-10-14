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
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication.Tests;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "78")]
public sealed class OrganisationTimeZoneEndpointTests
{
    private const string Issuer = "https://synthetic-aks-issuer.example.invalid/cluster-a/";
    private const string Audience = "zeka-timezone-test-only";
    private const string Subject = "system:serviceaccount:zeka-test:clientmanagement-test";

    [Fact]
    public async Task Write_ingress_rejects_configuration_based_activation()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConfigureTimeZoneIngress:Enabled"] = "true" });
        await using var app = builder.Build();
        Assert.Throws<InvalidOperationException>(() => app.MapDisabledConfigureTimeZone());
    }

    [Fact]
    public async Task Service_only_read_and_disabled_write_ingress_fail_closed()
    {
        await using var host = await TestHost.StartAsync();
        var request = new OrganisationTimeZoneRequest(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(
            "/internal/v1/organisation-time-zone", null, request)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostAsync(
            "/internal/v1/organisation-time-zone", host.Token("user"), request)).StatusCode);
        using var response = await host.PostAsync(
            "/internal/v1/organisation-time-zone", host.Token("service"), request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "configVersion", "contractVersion", "observedAtUtc", "organisationId", "timeZoneId" },
            body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(1, host.Resolver.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync(
            "/internal/v1/configure-time-zone", host.Token("service"), request)).StatusCode);
        Assert.Equal(1, host.Resolver.Calls);

        host.Resolver.Decision = null;
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostAsync(
            "/internal/v1/organisation-time-zone", host.Token("service"), request)).StatusCode);
        host.Resolver.Decision = new(request.OrganisationId, "Europe/Brussels", 1,
            DateTimeOffset.UtcNow.AddSeconds(-10));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.PostAsync(
            "/internal/v1/organisation-time-zone", host.Token("service"), request)).StatusCode);
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly HttpClient http;
        private readonly RSA key;
        public FakeResolver Resolver { get; }

        private TestHost(WebApplication app, HttpClient http, RSA key, FakeResolver resolver)
        { this.app = app; this.http = http; this.key = key; Resolver = resolver; }

        public static async Task<TestHost> StartAsync()
        {
            var key = RSA.Create(2048);
            var resolver = new FakeResolver();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AssigneeServiceAuthentication:Enabled"] = "true",
                ["AssigneeServiceAuthentication:Issuer"] = Issuer,
                ["AssigneeServiceAuthentication:Audience"] = Audience,
                ["AssigneeServiceAuthentication:JwksUri"] = Issuer + "keys",
                ["AssigneeServiceAuthentication:ClientManagementSubject"] = Subject,
                ["Authentication:Audience"] = "zeka-user-only"
            });
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddAssigneeServiceAuthentication(builder.Configuration);
            builder.Services.AddHttpClient<AssigneeJwksSource>()
                .ConfigurePrimaryHttpMessageHandler(() => new StaticJwks(
                    AuthenticationFixture.PublicJwks(key, "synthetic-timezone-key")));
            builder.Services.AddSingleton<IOrganisationTimeZone>(resolver);
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapOrganisationTimeZone();
            app.MapDisabledConfigureTimeZone();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(address) }, key, resolver);
        }

        public string Token(string kind)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var credentials = new SigningCredentials(
                new RsaSecurityKey(key) { KeyId = "synthetic-timezone-key" }, SecurityAlgorithms.RsaSha256);
            var payload = new JwtPayload
            {
                ["iss"] = Issuer,
                ["aud"] = kind == "user" ? "zeka-user-only" : Audience,
                ["sub"] = Subject,
                ["iat"] = now, ["nbf"] = now - 1, ["exp"] = now + 600
            };
            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                new JwtHeader(credentials), payload));
        }

        public async Task<HttpResponseMessage> PostAsync(string path, string? token,
            OrganisationTimeZoneRequest request)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(request,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            };
            if (token is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await http.SendAsync(message);
        }

        public async ValueTask DisposeAsync()
        { http.Dispose(); await app.DisposeAsync(); key.Dispose(); }
    }

    private sealed class FakeResolver : IOrganisationTimeZone
    {
        public int Calls { get; private set; }
        public OrganisationTimeZoneDecision? Decision { get; set; }
        public Task<OrganisationTimeZoneDecision?> ResolveAsync(
            OrganisationTimeZoneRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Decision ?? (Calls == 1
                ? new OrganisationTimeZoneDecision(request.OrganisationId,
                    "Europe/Brussels", 1, DateTimeOffset.UtcNow) : null));
        }
    }

    private sealed class StaticJwks(string document) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(document) });
    }
}
