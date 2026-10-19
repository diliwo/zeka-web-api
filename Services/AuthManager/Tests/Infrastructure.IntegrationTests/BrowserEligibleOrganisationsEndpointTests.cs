using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Runtime.ExceptionServices;
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
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Zeka.Authentication.Tests;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "89")]
[Trait("Slice", "Discovery")]
public sealed class BrowserEligibleOrganisationsEndpointTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("configure", false)] [InlineData("build", false)] [InlineData("start", false)]
    [InlineData("handler", false)] [InlineData("client", false)] [InlineData("return", false)]
    [InlineData("return", true)]
    public async Task Early_startup_failure_releases_owned_resources_and_preserves_original_failure(string stage, bool cleanupFails)
    {
        var original = new InvalidOperationException("Synthetic startup failure: " + stage);
        var owned = new StartupOwnership();
        var callerOwned = new DisposalProbe(false);
        var error = await Record.ExceptionAsync(() => DiscoveryEndpointHost.StartAsync((builder, _) =>
        {
            builder.Services.AddSingleton(callerOwned);
            if (stage == "configure") throw original;
            if (stage == "build")
            {
                builder.Host.UseDefaultServiceProvider(options => options.ValidateOnBuild = true);
                builder.Services.AddSingleton<InvalidStartupService>();
            }
            if (stage == "start") builder.Services.AddSingleton<IHostedService>(new FailingStartupService(original));
            if (cleanupFails) builder.Services.AddSingleton(_ => new DisposalProbe(true));
        }, (point, resource) =>
        {
            owned.Acquired(point, resource);
            if (cleanupFails && point == "application")
                ((WebApplication)resource).Services.GetRequiredService<DisposalProbe>();
            if (point == stage && stage is "handler" or "client" or "return") throw original;
        }, () => new ObservedHandler(cleanupFails)));
        if (stage == "build") Assert.Contains(nameof(UnregisteredStartupDependency), error!.ToString());
        else Assert.Same(original, error);
        owned.AssertDisposed();
        Assert.False(callerOwned.Disposed);
        output.WriteLine($"startup_failure={stage} cleanup_failure={cleanupFails} original_preserved=true owned_disposal_verified=true {owned.Inventory}");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Successful_startup_and_disposal_release_all_owned_resources_independently(bool cleanupFails)
    {
        var owned = new StartupOwnership();
        var host = await DiscoveryEndpointHost.StartAsync(acquired: owned.Acquired,
            createHandler: () => new ObservedHandler(cleanupFails));
        await host.AssertAsync(HttpStatusCode.Unauthorized);
        var error = await Record.ExceptionAsync(async () => await host.DisposeAsync());
        if (cleanupFails) Assert.IsType<InvalidOperationException>(error);
        else Assert.Null(error);
        owned.AssertDisposed();
        output.WriteLine($"normal_disposal cleanup_failure={cleanupFails} owned_disposal_verified=true {owned.Inventory}");
    }

    private sealed class StartupOwnership
    {
        private AuthenticationFixture? auth;
        private WebApplication? app;
        private HttpClient? client;
        private ObservedHandler? handler;
        internal string Inventory => $"auth_acquired={auth is not null} app_acquired={app is not null} client_acquired={client is not null} handler_acquired={handler is not null} handler_disposed={handler?.Disposed}";
        internal void Acquired(string stage, object value)
        {
            if (stage == "auth") auth = (AuthenticationFixture)value;
            if (stage == "application") app = (WebApplication)value;
            if (stage == "handler") handler = (ObservedHandler)value;
            if (stage == "client") client = (HttpClient)value;
        }
        internal void AssertDisposed()
        {
            Assert.NotNull(auth);
            Assert.NotNull(Record.Exception(() => auth.Key.ExportParameters(false)));
            if (app is not null) Assert.Throws<ObjectDisposedException>(() => app.Services.GetService<IHostApplicationLifetime>());
            if (client is not null) Assert.Throws<ObjectDisposedException>(() => client.CancelPendingRequests());
            if (handler is not null) Assert.True(handler.Disposed);
        }
    }
    private sealed class ObservedHandler(bool failDispose) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true; base.Dispose(disposing);
            if (failDispose) throw new InvalidOperationException("Synthetic handler cleanup failure");
        }
    }
    private sealed class DisposalProbe(bool failDispose) : IDisposable
    {
        internal bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; if (failDispose) throw new InvalidOperationException("Synthetic application cleanup failure"); }
    }
    private sealed class UnregisteredStartupDependency;
    private sealed class InvalidStartupService(UnregisteredStartupDependency dependency) { internal object Dependency => dependency; }
    private sealed class FailingStartupService(Exception original) : IHostedService
    {
        public Task StartAsync(CancellationToken token) => Task.FromException(original);
        public Task StopAsync(CancellationToken token) => Task.CompletedTask;
    }

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
    DocumentSource source, HttpClient http, DiscoveryChoices choices, DiscoverySessions sessions, HttpMessageHandler handler) : IAsyncDisposable
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
    internal static async Task<DiscoveryEndpointHost> StartAsync(Action<WebApplicationBuilder, AuthenticationFixture>? configure = null,
        Action<string, object>? acquired = null, Func<HttpMessageHandler>? createHandler = null)
    {
        AuthenticationFixture? auth = null; WebApplication? app = null; HttpClient? http = null; HttpMessageHandler? handler = null;
        try
        {
            var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            auth = new AuthenticationFixture(); acquired?.Invoke("auth", auth);
            var source = new DocumentSource(auth.Jwks());
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
            app = builder.Build(); acquired?.Invoke("application", app);
            app.UseAuthentication(); app.UseAuthorization();
            app.MapBrowserEligibleOrganisations();
            // Real selected-tenant route is mapped only for provider tests that register its dependencies.
            if (mapTenantAccess) app.MapTenantAccess();
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            handler = createHandler?.Invoke() ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            acquired?.Invoke("handler", handler);
            http = new HttpClient(handler, disposeHandler: false);
            http.BaseAddress = new Uri(address);
            acquired?.Invoke("client", http);
            acquired?.Invoke("return", app);
            return new(app, auth, source, http, choices, sessions, handler);
        }
        catch
        {
            await DisposeOwnedAsync(http, handler, app, auth, suppressErrors: true);
            throw;
        }
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
    private bool disposed;
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await DisposeOwnedAsync(http, handler, app, auth, suppressErrors: false);
    }
    private static async Task DisposeOwnedAsync(HttpClient? client, HttpMessageHandler? handler,
        WebApplication? application, AuthenticationFixture? authentication, bool suppressErrors)
    {
        Exception? first = null;
        try { client?.Dispose(); } catch (Exception error) { first ??= error; }
        try { handler?.Dispose(); } catch (Exception error) { first ??= error; }
        try { if (application is not null) await application.DisposeAsync(); } catch (Exception error) { first ??= error; }
        try { authentication?.Dispose(); } catch (Exception error) { first ??= error; }
        if (!suppressErrors && first is not null) ExceptionDispatchInfo.Capture(first).Throw();
    }
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
