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
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proxy_forwards_exact_json_and_content_type_for_known_length_and_chunked_bodies(bool knownLength)
    {
        await using var test = await Host.StartAsync();
        var csrf = await test.CsrfAsync();
        const string body = "{\"email\":\"fictional@example.invalid\",\"password\":\"Synthetic-Only!89\"}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/browser-session/sign-in");
        request.Content = knownLength ? new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(body))
            : JsonContent.Create(new { email = "fictional@example.invalid", password = "Synthetic-Only!89" });
        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json; charset=utf-8");
        request.Headers.Add("Origin", "https://app.example.invalid");
        request.Headers.Add("X-Zeka-CSRF", csrf);
        using var response = await test.Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body, test.Received.Body);
        Assert.Equal("application/json; charset=utf-8", test.Received.ContentType);
        Assert.Equal(knownLength, test.Received.ProxyContentLength.HasValue);
        Assert.Equal(!knownLength, test.Received.ProxyChunked);
        Assert.Equal("fictional@example.invalid", test.Sessions.LastEmail);
        Assert.Equal(1, test.Sessions.Mutations);
        using var restored = await test.PostAsync("restore", csrf);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(test.Sessions.RefreshSecret, test.Sessions.LastCookie);
        Assert.Equal(2, test.Sessions.Mutations);
    }

    private sealed class ReceivedRequest
    {
        public string? Body { get; set; }
        public string? ContentType { get; set; }
        public long? ProxyContentLength { get; set; }
        public bool ProxyChunked { get; set; }
    }

    private sealed class Sessions : IBrowserSessions
    {
        public BrowserSessionOutcome Outcome { get; set; } = BrowserSessionOutcome.Success;
        public string RefreshSecret { get; } = new('a', 43);
        public string? LastCookie { get; private set; }
        public int Mutations { get; private set; }
        public string? LastEmail { get; private set; }
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken)
        { Mutations++; LastEmail = email; return Result(); }
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken)
        { Mutations++; LastCookie = cookie; return Result(); }
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken)
        { Mutations++; LastCookie = cookie; return Task.FromResult(Outcome); }
        public Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        private Task<BrowserSessionResult> Result() => Task.FromResult(new BrowserSessionResult { Outcome = Outcome,
            AccessToken = "synthetic-memory-bearer", RefreshSecret = RefreshSecret, SubjectId = Guid.NewGuid(),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5), CookieExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(8) });
    }

    [Fact]
    public async Task Proxy_strips_forgery_and_separates_addresses_and_csrf_from_mutations()
    {
        await using var test = await Host.StartAsync();
        var csrf = await test.CsrfAsync();
        test.Http.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.99, 203.0.113.100");
        test.Http.DefaultRequestHeaders.Add("Forwarded", "for=untrusted");
        for (var i = 0; i < 60; i++) using (await test.Http.GetAsync("/api/v1/browser-session/csrf")) { }
        using var exhausted = await test.Http.GetAsync("/api/v1/browser-session/csrf");
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);
        Assert.False(exhausted.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, (await test.PostAsync("sign-in", csrf)).StatusCode);
        using var another = test.Client("127.0.0.3");
        Assert.Equal(HttpStatusCode.OK, (await another.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
        using var shared = test.Client("127.0.0.2");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await shared.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
        Assert.Equal(3, test.Admission.PartitionCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("198.51.100.1, 198.51.100.2")]
    public async Task Invalid_attribution_rejects_before_credentials_or_partitions(string? header)
    {
        await using var test = await Host.StartAsync();
        using var direct = test.Client("127.0.0.1", direct: true);
        if (header is not null) direct.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", header);
        using var response = await direct.GetAsync("/api/v1/browser-session/csrf");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, test.Admission.PartitionCount); Assert.Equal(0, test.Sessions.Mutations);
    }

    [Fact]
    public async Task Duplicate_untrusted_and_missing_trust_never_allocate_partitions_or_write_sessions()
    {
        await using (var test = await Host.StartAsync())
        {
            using var direct = test.Client("127.0.0.1", direct: true);
            var duplicate = new HttpRequestMessage(HttpMethod.Get, "/api/v1/browser-session/csrf");
            duplicate.Headers.TryAddWithoutValidation("X-Forwarded-For", new[] { "198.51.100.1", "198.51.100.2" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await direct.SendAsync(duplicate)).StatusCode);
            using var attacker = test.Client("127.0.0.2", direct: true);
            attacker.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.1");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await attacker.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await attacker.GetAsync("/unrelated")).StatusCode);
            Assert.Equal(0, test.Admission.PartitionCount);
        }
        foreach (var peer in new string?[] { null, "invalid" })
        {
            await using var test = await Host.StartAsync(values => values["BrowserSession:TrustedProxyPeers:0"] = peer);
            using var response = await test.Http.GetAsync("/api/v1/browser-session/csrf");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            Assert.Equal(0, test.Admission.PartitionCount); Assert.Equal(0, test.Sessions.Mutations);
        }
    }

    [Fact]
    public async Task Canonical_mapped_addresses_share_allowance_and_churn_remains_bounded()
    {
        await using var test = await Host.StartAsync(values => values["BrowserSession:Limits:PerAddress"] = "1");
        using var direct = test.Client("127.0.0.1", direct: true);
        async Task<HttpStatusCode> Get(string address)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/browser-session/csrf");
            request.Headers.Add("X-Forwarded-For", address);
            using var response = await direct.SendAsync(request); return response.StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await Get("198.51.100.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get("::ffff:198.51.100.1"));
        for (var i = 2; i < 40; i++) await Get($"198.51.100.{i}");
        Assert.Equal(16, test.Admission.PartitionCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, await Get("203.0.113.1"));
    }

    [Fact]
    public async Task Aggregate_overload_is_separate_from_each_address_ordinary_allowance()
    {
        await using var test = await Host.StartAsync(values =>
        { values["BrowserSession:Limits:PerAddress"] = "1"; values["BrowserSession:Limits:Aggregate"] = "2"; });
        using var one = test.Client("127.0.0.2"); using var two = test.Client("127.0.0.3"); using var three = test.Client("127.0.0.4");
        Assert.Equal(HttpStatusCode.OK, (await one.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await two.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await three.GetAsync("/api/v1/browser-session/csrf")).StatusCode);
        Assert.Equal(0, test.Sessions.Mutations);
    }

    private sealed class Host(WebApplication app, WebApplication proxy, HttpClient upstream,
        HttpClient http, Sessions sessions, X509Certificate2 certificate, ReceivedRequest received) : IAsyncDisposable
    {
        public HttpClient Http => http;
        public Sessions Sessions => sessions;
        public ReceivedRequest Received => received;
        public BrowserSessionEndpoints.BrowserSessionAdmission Admission => app.Services.GetRequiredService<BrowserSessionEndpoints.BrowserSessionAdmission>();
        public HttpClient Client(string localAddress, bool direct = false)
            => CreateClient(direct ? Address(app) : Address(proxy), localAddress, certificate);
        public static async Task<Host> StartAsync(Action<Dictionary<string, string?>>? configure = null)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            var values = new Dictionary<string, string?> { ["BrowserSession:Origin"] = "https://app.example.invalid",
                ["BrowserSession:TrustedProxyPeers:0"] = "127.0.0.1", ["BrowserSession:Limits:PerAddress"] = "60",
                ["BrowserSession:Limits:MaxPartitions"] = "16", ["BrowserSession:Limits:Aggregate"] = "256",
                ["BrowserSession:Limits:WindowSeconds"] = "60" };
            configure?.Invoke(values); builder.Configuration.AddInMemoryCollection(values);
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBrowserSessionTransport();
            var sessions = new Sessions();
            builder.Services.AddSingleton<IBrowserSessions>(sessions);
            var app = builder.Build();
            var received = new ReceivedRequest();
            app.Use(async (context, next) =>
            {
                if (HttpMethods.IsPost(context.Request.Method))
                {
                    context.Request.EnableBuffering();
                    using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                    received.Body = await reader.ReadToEndAsync();
                    received.ContentType = context.Request.ContentType;
                    context.Request.Body.Position = 0;
                }
                await next(context);
            });
            app.UseBrowserSessionAttribution(); app.UseRateLimiter(); app.MapBrowserSessions();
            app.MapGet("/unrelated", () => Results.Ok()); await app.StartAsync();
            var upstream = CreateClient(Address(app), "127.0.0.1", certificate, cookies: false);
            var proxyBuilder = WebApplication.CreateBuilder(); proxyBuilder.Logging.ClearProviders();
            proxyBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            var proxy = proxyBuilder.Build();
            proxy.Run(async context =>
            {
                using var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
                received.ProxyContentLength = context.Request.ContentLength;
                received.ProxyChunked = context.Request.Headers.TransferEncoding.Contains("chunked");
                if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0
                    || context.Request.ContentType is not null)
                {
                    forwarded.Content = new StreamContent(context.Request.Body);
                    forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
                // This controlled single-hop boundary discards all caller forwarding metadata.
                foreach (var header in new[] { "Origin", "X-Zeka-CSRF", "Cookie" })
                    if (context.Request.Headers.TryGetValue(header, out var value)) forwarded.Headers.TryAddWithoutValidation(header, value.ToArray());
                forwarded.Headers.Add("X-Forwarded-For", context.Connection.RemoteIpAddress!.MapToIPv4().ToString());
                using var response = await upstream.SendAsync(forwarded);
                context.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers)
                    if (header.Key is "Set-Cookie" or "Cache-Control" or "Pragma") context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                await response.Content.CopyToAsync(context.Response.Body);
            });
            await proxy.StartAsync();
            return new(app, proxy, upstream, CreateClient(Address(proxy), "127.0.0.2", certificate), sessions, certificate, received);
        }
        private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        private static HttpClient CreateClient(string address, string localAddress, X509Certificate2 certificate, bool cookies = true)
        {
            var handler = new SocketsHttpHandler { UseCookies = cookies, CookieContainer = new CookieContainer(), UseProxy = false,
                SslOptions = new() { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == certificate.Thumbprint },
                ConnectCallback = async (context, cancellationToken) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
                        await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                } };
            return new HttpClient(handler) { BaseAddress = new(address), Timeout = TimeSpan.FromSeconds(15) };
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
        public async ValueTask DisposeAsync()
        { http.Dispose(); upstream.Dispose(); await proxy.DisposeAsync(); await app.DisposeAsync(); certificate.Dispose(); }
    }
}
