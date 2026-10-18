using System.Threading.RateLimiting;
using AuthManager.Application.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;

namespace AuthManager.API.Endpoints;

public static class BrowserSessionEndpoints
{
    public const string CookieName = "__Secure-ZekaRefresh";
    public const string CookiePath = "/api/v1/browser-session";
    private const string CallerAddress = "Zeka.BrowserSession.CallerAddress";

    public static void AddBrowserSessionTransport(this IServiceCollection services)
    {
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-Zeka-CSRF";
            options.Cookie.Name = "__Secure-ZekaCSRF";
            options.Cookie.Path = CookiePath;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddRateLimiter(_ => { });
        services.AddSingleton<BrowserSessionAdmission>();
    }

    public static void UseBrowserSessionAttribution(this WebApplication app)
    {
        var admission = app.Services.GetRequiredService<BrowserSessionAdmission>();
        app.UseWhen(context => context.Request.Path.StartsWithSegments(CookiePath), branch =>
        {
            branch.Use(async (http, next) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                if (!admission.TryAttribute(http, out var address))
                { http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
                http.Items[CallerAddress] = address;
                await next(http);
            });
            branch.UseForwardedHeaders(admission.Forwarding);
            branch.Use(async (http, next) =>
            {
                var address = (string)http.Items[CallerAddress]!;
                if (Canonical(http.Connection.RemoteIpAddress) != address)
                { http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
                if (!admission.Acquire(address, HttpMethods.IsGet(http.Request.Method)))
                { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
                await next(http);
            });
        });
    }

    // Receiving-side policy has no production defaults. Only explicitly configured exact proxy peers are trusted.
    public sealed class BrowserSessionAdmission : IDisposable
    {
        private readonly HashSet<string> peers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (FixedWindowRateLimiter Limiter, DateTimeOffset Used)>[] partitions = [new(), new()];
        private readonly FixedWindowRateLimiter[] aggregate = [];
        private readonly object gate = new();
        private readonly int perAddress;
        private readonly int capacity;
        private readonly TimeSpan window;
        private readonly bool valid;
        public ForwardedHeadersOptions Forwarding { get; } = new()
        { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
        public int PartitionCount { get { lock (gate) return partitions.Sum(budget => budget.Count); } }
        public BrowserSessionAdmission(IConfiguration configuration)
        {
            Forwarding.KnownNetworks.Clear(); Forwarding.KnownProxies.Clear();
            var configured = configuration.GetSection("BrowserSession:TrustedProxyPeers").Get<string[]>() ?? [];
            foreach (var peer in configured)
            {
                if (!IPAddress.TryParse(peer, out var address) || peer.Contains('%') || Canonical(address) is not { } canonical
                    || !peers.Add(canonical)) return;
                Forwarding.KnownProxies.Add(address);
            }
            // Invalid typed values also fail closed, rather than throwing after the host is built.
            if (!int.TryParse(configuration["BrowserSession:Limits:PerAddress"], out perAddress)
                || !int.TryParse(configuration["BrowserSession:Limits:MaxPartitions"], out capacity)
                || !int.TryParse(configuration["BrowserSession:Limits:Aggregate"], out var total)
                || !int.TryParse(configuration["BrowserSession:Limits:WindowSeconds"], out var seconds)
                || peers.Count == 0 || perAddress is < 1 or > 10000 || capacity is < 1 or > 4096
                || total < perAddress || total > 100000 || seconds is < 1 or > 3600) return;
            window = TimeSpan.FromSeconds(seconds);
            aggregate = [Create(total), Create(total)];
            valid = true;
        }
        public bool TryAttribute(HttpContext http, out string address)
        {
            address = "";
            if (!valid || Canonical(http.Connection.RemoteIpAddress) is not { } peer || !peers.Contains(peer)
                || http.Request.Headers["X-Forwarded-For"].Count != 1) return false;
            var raw = http.Request.Headers["X-Forwarded-For"][0];
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains(',') || raw.Contains('%')
                || !IPAddress.TryParse(raw, out var client) || Canonical(client) is not { } canonical) return false;
            foreach (var header in new[] { "Forwarded", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix", "X-Original-For" })
                if (http.Request.Headers.ContainsKey(header)) return false;
            address = canonical;
            return true;
        }
        public bool Acquire(string address, bool csrf)
        {
            lock (gate)
            {
                var index = csrf ? 0 : 1;
                var budget = partitions[index];
                var now = DateTimeOffset.UtcNow;
                if (!budget.TryGetValue(address, out var entry))
                {
                    foreach (var stale in budget.Where(x => now - x.Value.Used >= window + window).Select(x => x.Key).ToArray())
                    { budget[stale].Limiter.Dispose(); budget.Remove(stale); }
                    if (budget.Count >= capacity) return false;
                    entry = (Create(perAddress), now);
                }
                budget[address] = (entry.Limiter, now);
                using var ordinary = entry.Limiter.AttemptAcquire();
                if (!ordinary.IsAcquired) return false;
                using var overload = aggregate[index].AttemptAcquire();
                return overload.IsAcquired;
            }
        }
        private FixedWindowRateLimiter Create(int permits) => new(new FixedWindowRateLimiterOptions
        { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true });
        public void Dispose()
        {
            lock (gate)
            {
                foreach (var budget in partitions) foreach (var entry in budget.Values) entry.Limiter.Dispose();
                if (aggregate is not null) foreach (var limiter in aggregate) limiter.Dispose();
            }
        }
    }
    private static string? Canonical(IPAddress? address) => address?.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;

    public static void MapBrowserSessions(this WebApplication app)
    {
        var group = app.MapGroup(CookiePath);
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsPost(http.Request.Method)) return await next(context);
            var expected = app.Configuration["BrowserSession:Origin"];
            if (!ValidOrigin(expected)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (http.Request.Headers.Origin.Count != 1
                || !string.Equals(http.Request.Headers.Origin[0], expected, StringComparison.Ordinal)
                || !http.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
            catch (AntiforgeryValidationException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            return await next(context);
        });
        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            try { return Results.Ok(new { ContractVersion = 1, CsrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }); }
            catch (System.Security.Cryptography.CryptographicException) { return Results.StatusCode(503); }
        });
        group.MapPost("/sign-in", async (SignInRequest input, IBrowserSessions sessions, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(input.Email) || input.Email.Length > 256
                || string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024) return Results.Unauthorized();
            var result = await sessions.SignInAsync(input.Email, input.Password, http.Request.Cookies[CookieName], http.RequestAborted);
            return Respond(http, result, clearDenied: false);
        });
        group.MapPost("/restore", async (IBrowserSessions sessions, HttpContext http) =>
            Respond(http, await sessions.RestoreAsync(http.Request.Cookies[CookieName], http.RequestAborted), clearDenied: true));
        group.MapPost("/logout", async (IBrowserSessions sessions, HttpContext http) =>
        {
            var outcome = await sessions.LogoutAsync(http.Request.Cookies[CookieName], http.RequestAborted);
            if (outcome == BrowserSessionOutcome.Unavailable) return Results.StatusCode(503);
            if (outcome != BrowserSessionOutcome.Success) return Results.Unauthorized();
            http.Response.Cookies.Delete(CookieName, CookieOptions());
            return Results.NoContent();
        });
    }
    private static IResult Respond(HttpContext http, BrowserSessionResult result, bool clearDenied)
    {
        if (result.Outcome == BrowserSessionOutcome.Unavailable) return Results.StatusCode(503);
        if (result.Outcome != BrowserSessionOutcome.Success)
        {
            if (clearDenied) http.Response.Cookies.Delete(CookieName, CookieOptions());
            return Results.Unauthorized();
        }
        var options = CookieOptions(); options.Expires = result.CookieExpiresAtUtc;
        http.Response.Cookies.Append(CookieName, result.RefreshSecret!, options);
        return Results.Ok(new { ContractVersion = 1, result.SubjectId, result.AccessToken, result.ExpiresAtUtc });
    }
    private static CookieOptions CookieOptions() => new()
    { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = CookiePath, IsEssential = true };
    private static bool ValidOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/"
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && value == uri.GetLeftPart(UriPartial.Authority);
    public sealed class SignInRequest
    {
        public string Email { get; init; } = "";
        public string Password { get; init; } = "";
        public override string ToString() => "Browser sign-in request (redacted)";
    }
}
