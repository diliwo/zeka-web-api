using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "89")]
public sealed class BrowserSessionTransportTests
{
    [Fact]
    public async Task Mutation_requires_exact_origin_json_and_framework_csrf_and_returns_only_memory_bearer()
    {
        await using var test = await Host.StartAsync();
        var csrf = await test.CsrfAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await test.PostAsync("sign-in", csrf, origin: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await test.PostAsync("sign-in", csrf, origin: "https://evil.example.invalid")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await test.PostAsync("sign-in", "invalid")).StatusCode);
        Assert.Equal(0, test.Sessions.Mutations);
        using var response = await test.PostAsync("sign-in", csrf);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(test.Sessions.RefreshSecret, json);
        Assert.Contains("accessToken", json);
        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/v1/browser-session", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_logout_retains_cookie_and_fresh_csrf_retry_can_confirm_revocation_without_bearer()
    {
        await using var test = await Host.StartAsync();
        var csrf = await test.CsrfAsync();
        Assert.Equal(HttpStatusCode.OK, (await test.PostAsync("sign-in", csrf)).StatusCode);
        test.Sessions.Outcome = BrowserSessionOutcome.Unavailable;
        using var failed = await test.PostAsync("logout", csrf);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal(test.Sessions.RefreshSecret, test.Sessions.LastCookie);
        csrf = await test.CsrfAsync();
        test.Sessions.Outcome = BrowserSessionOutcome.Success;
        using var retry = await test.PostAsync("logout", csrf);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Contains("expires=", retry.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Null(test.Http.DefaultRequestHeaders.Authorization);
        test.Sessions.Outcome = BrowserSessionOutcome.Unauthenticated;
        using var lostCookie = await test.PostAsync("logout", await test.CsrfAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, lostCookie.StatusCode);
        Assert.Null(test.Sessions.LastCookie);
        Assert.False(lostCookie.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Restore_uncertainty_emits_no_token_or_cookie_and_rate_limit_is_bounded()
    {
        await using var test = await Host.StartAsync();
        var csrf = await test.CsrfAsync();
        test.Sessions.Outcome = BrowserSessionOutcome.Unavailable;
        using var failed = await test.PostAsync("restore", csrf);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.False(failed.Headers.Contains("Set-Cookie"));
        Assert.Equal("", await failed.Content.ReadAsStringAsync());
        var responses = new List<HttpStatusCode>();
        for (var i = 0; i < 65; i++)
        {
            using var response = await test.Http.GetAsync("/api/v1/browser-session/csrf");
            responses.Add(response.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, responses);
    }

    private sealed class Sessions : IBrowserSessions
    {
        public BrowserSessionOutcome Outcome { get; set; } = BrowserSessionOutcome.Success;
        public string RefreshSecret { get; } = new('a', 43);
        public string? LastCookie { get; private set; }
        public int Mutations { get; private set; }
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken)
        { Mutations++; return Result(); }
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken)
        { Mutations++; LastCookie = cookie; return Result(); }
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken)
        { Mutations++; LastCookie = cookie; return Task.FromResult(Outcome); }
        public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        private Task<BrowserSessionResult> Result() => Task.FromResult(new BrowserSessionResult { Outcome = Outcome,
            AccessToken = "synthetic-memory-bearer", RefreshSecret = RefreshSecret, SubjectId = Guid.NewGuid(),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5), CookieExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(8) });
    }

    private sealed class Host(WebApplication app, HttpClient http, Sessions sessions, X509Certificate2 certificate) : IAsyncDisposable
    {
        public HttpClient Http => http;
        public Sessions Sessions => sessions;
        public static async Task<Host> StartAsync()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["BrowserSession:Origin"] = "https://app.example.invalid" });
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBrowserSessionTransport();
            var sessions = new Sessions();
            builder.Services.AddSingleton<IBrowserSessions>(sessions);
            var app = builder.Build();
            app.UseRateLimiter(); app.MapBrowserSessions(); await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(),
                ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == certificate.Thumbprint })
                { BaseAddress = new(address) };
            return new(app, http, sessions, certificate);
        }
        public async Task<string> CsrfAsync()
        {
            using var response = await http.GetAsync("/api/v1/browser-session/csrf");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("csrfToken").GetString()!;
        }
        public Task<HttpResponseMessage> PostAsync(string action, string csrf, string? origin = "https://app.example.invalid")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/browser-session/" + action)
                { Content = JsonContent.Create(new { email = "fictional@example.invalid", password = "Synthetic-Only!89" }) };
            request.Headers.Add("X-Zeka-CSRF", csrf);
            if (origin is not null) request.Headers.Add("Origin", origin);
            return http.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { http.Dispose(); await app.DisposeAsync(); certificate.Dispose(); }
    }
}
