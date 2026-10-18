using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
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

[Trait("Issue", "89")]
[Trait("Slice", "Discovery")]
public sealed class BrowserEligibleOrganisationsEndpointTests
{
    [Fact]
    public async Task Bearer_only_gate_rejects_missing_expired_ambiguous_and_subject_only_credentials()
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        host.Http.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Identity.Application=fictional; zeka-refresh=fictional");
        await host.AssertAsync(HttpStatusCode.Unauthorized);
        var valid = new[] { new Claim("sub", host.Subject.ToString()), new Claim("sid", host.Session.ToString()) };
        await host.AssertAsync(HttpStatusCode.Unauthorized, host.Token(valid, expired: true));
        foreach (var claims in new Claim[][] {
            [valid[0]], [valid[1]], [new("sub", "bad"), valid[1]],
            [new("sub", Guid.Empty.ToString()), valid[1]], [valid[0], new("sid", "bad")],
            [valid[0], new("sid", Guid.Empty.ToString())],
            [..valid, new("sub", Guid.NewGuid().ToString())],
            [..valid, new("sid", Guid.NewGuid().ToString())],
            [..valid, new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())] })
            await host.AssertAsync(HttpStatusCode.Unauthorized, host.Token(claims));
        Assert.Equal(0, host.Choices.Calls);
        Assert.Equal(0, host.Sessions.Calls);
    }

    [Theory]
    [InlineData("")] [InlineData("not-a-guid")]
    public async Task Selector_header_presence_is_rejected_without_query_or_echo(string value)
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, DiscoveryEndpointHost.Path);
        request.Headers.Authorization = new("Bearer", host.ValidToken());
        request.Headers.TryAddWithoutValidation("X-Organisation-Id", value);
        using var response = await host.Http.SendAsync(request);
        await DiscoveryEndpointHost.AssertErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, host.Choices.Calls);
    }

    [Theory]
    [InlineData("?organisationId=")] [InlineData("?organisationId=not-a-guid")]
    [InlineData("?OrganisationId=ffffffff-ffff-ffff-ffff-ffffffffffff")]
    [InlineData("?tenant=")] [InlineData("?unexpected=value")]
    public async Task No_query_input_can_select_a_tenant(string query)
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        await host.AssertAsync(HttpStatusCode.BadRequest, host.ValidToken(), query);
        Assert.Equal(0, host.Choices.Calls);
    }

    [Fact]
    public async Task Empty_and_nonempty_success_have_only_the_complete_v1_contract_and_no_cookie()
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        foreach (var choices in new EligibleOrganisation[][] { [], [new(Guid.NewGuid(), "Alpha")] })
        {
            host.Choices.Result = new(EligibleOrganisationsOutcome.Success, choices);
            using var response = await host.GetAsync(host.ValidToken());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "contractVersion", "subjectId", "organisations" }, json.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(1, json.RootElement.GetProperty("contractVersion").GetInt32());
            Assert.Equal(host.Subject, json.RootElement.GetProperty("subjectId").GetGuid());
            var array = json.RootElement.GetProperty("organisations"); Assert.Equal(choices.Length, array.GetArrayLength());
            if (choices.Length > 0)
                Assert.Equal(new[] { "organisationId", "displayName" }, array[0].EnumerateObject().Select(p => p.Name));
            Assert.Equal(host.Subject, host.Choices.LastSubject);
        }
    }

    [Theory]
    [InlineData("revoked", 401)] [InlineData("session-unavailable", 503)]
    [InlineData("inactive-user", 401)] [InlineData("overflow", 409)]
    [InlineData("database", 503)] [InlineData("deadline", 503)] [InlineData("resolver-unavailable", 503)]
    public async Task Errors_are_generic_no_store_and_never_return_partial_choices(string failure, int status)
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        if (failure == "revoked") host.Sessions.Outcome = BrowserSessionOutcome.Unauthenticated;
        if (failure == "session-unavailable") host.Sessions.Outcome = BrowserSessionOutcome.Unavailable;
        host.Choices.Failure = failure;
        if (failure == "inactive-user") host.Choices.Result = new(EligibleOrganisationsOutcome.Unauthenticated, []);
        if (failure == "overflow") host.Choices.Result = new(EligibleOrganisationsOutcome.Overflow, [new(Guid.NewGuid(), "must-not-leak")]);
        if (failure == "resolver-unavailable") host.Choices.Result = new(EligibleOrganisationsOutcome.Unavailable, []);
        await host.AssertAsync((HttpStatusCode)status, host.ValidToken());
        if (failure is "revoked" or "session-unavailable") Assert.Equal(0, host.Choices.Calls);
    }

    [Fact]
    public async Task Signing_authority_unavailability_uses_existing_generic_503_challenge()
    {
        await using var host = await DiscoveryEndpointHost.StartAsync();
        host.Source.Unavailable = true;
        await host.AssertAsync(HttpStatusCode.ServiceUnavailable, host.ValidToken());
        Assert.Equal(0, host.Choices.Calls); Assert.Equal(0, host.Sessions.Calls);
    }
}

internal sealed class DiscoveryEndpointHost(WebApplication app, AuthenticationFixture auth,
    DocumentSource source, HttpClient http, DiscoveryChoices choices, DiscoverySessions sessions) : IAsyncDisposable
{
    internal const string Path = "/api/v1/browser-organisations/eligible";
    internal Guid Subject { get; } = Guid.Parse("89000000-0000-0000-0000-000000000001");
    internal Guid Session { get; } = Guid.Parse("89000000-0000-0000-0000-000000000002");
    internal HttpClient Http => http;
    internal AuthenticationFixture Auth => auth;
    internal DocumentSource Source => source;
    internal DiscoveryChoices Choices => choices;
    internal DiscoverySessions Sessions => sessions;
    internal IServiceProvider Services => app.Services;
    internal static async Task<DiscoveryEndpointHost> StartAsync(Action<WebApplicationBuilder, AuthenticationFixture>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var auth = new AuthenticationFixture(); var source = new DocumentSource(auth.Jwks());
        builder.Configuration.AddConfiguration(auth.Configuration());
        builder.Services.AddAuthentication(options => { options.DefaultAuthenticateScheme = "identity-cookie"; options.DefaultChallengeScheme = "identity-cookie"; }).AddCookie("identity-cookie");
        builder.Services.AddTenantAuthentication(auth.Configuration());
        builder.Services.AddSingleton<Zeka.Authentication.IJwksDocumentSource>(source);
        builder.Services.AddSingleton<TimeProvider>(auth.Clock);
        var choices = new DiscoveryChoices(); var sessions = new DiscoverySessions();
        builder.Services.AddSingleton<IEligibleOrganisations>(choices);
        builder.Services.AddSingleton<IBrowserSessions>(sessions);
        configure?.Invoke(builder, auth);
        var mapTenantAccess = builder.Services.Any(service => service.ServiceType == typeof(ICurrentTenantAccess));
        var app = builder.Build(); app.UseAuthentication(); app.UseAuthorization();
        app.MapBrowserEligibleOrganisations();
        // Real selected-tenant route is mapped only for provider tests that register its dependencies.
        if (mapTenantAccess) app.MapTenantAccess();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, auth, source, new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = new Uri(address) }, choices, sessions);
    }
    internal string ValidToken() => Token([new("sub", Subject.ToString()), new("sid", Session.ToString())]);
    internal string Token(IEnumerable<Claim> claims, bool expired = false)
    {
        var token = new JwtSecurityToken(AuthenticationFixture.Issuer, AuthenticationFixture.Audience, claims,
            DateTime.UtcNow.AddHours(-2), expired ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(auth.Key) { KeyId = AuthenticationFixture.KeyId }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    internal Task<HttpResponseMessage> GetAsync(string? token = null, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Path + query);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return http.SendAsync(request);
    }
    internal async Task AssertAsync(HttpStatusCode expected, string? token = null, string query = "")
    { using var response = await GetAsync(token, query); await AssertErrorAsync(response, expected); }
    internal static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal("", await response.Content.ReadAsStringAsync()); Assert.False(response.Headers.Contains("Set-Cookie"));
    }
    public async ValueTask DisposeAsync() { http.Dispose(); await app.DisposeAsync(); auth.Dispose(); }
}

internal sealed class DiscoveryChoices : IEligibleOrganisations
{
    internal int Calls { get; private set; }
    internal Guid LastSubject { get; private set; }
    internal string? Failure { get; set; }
    internal EligibleOrganisationsResult Result { get; set; } = new(EligibleOrganisationsOutcome.Success, []);
    public async Task<EligibleOrganisationsResult> ResolveAsync(Guid subject, CancellationToken cancellationToken = default)
    {
        Calls++; LastSubject = subject;
        if (Failure == "database") throw new Npgsql.NpgsqlException("private-database-detail");
        if (Failure == "deadline") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Result;
    }
}
internal sealed class DiscoverySessions : IBrowserSessions
{
    internal int Calls { get; private set; }
    internal BrowserSessionOutcome Outcome { get; set; } = BrowserSessionOutcome.Success;
    public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
    { Calls++; return Task.FromResult(Outcome); }
    public Task<BrowserSessionResult> SignInAsync(string email, string password, string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken) => throw new NotSupportedException();
}
