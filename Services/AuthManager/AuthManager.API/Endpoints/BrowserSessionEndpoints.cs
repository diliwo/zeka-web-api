using System.Threading.RateLimiting;
using AuthManager.Application.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthManager.API.Endpoints;

public static class BrowserSessionEndpoints
{
    public const string CookieName = "__Secure-ZekaRefresh";
    public const string CookiePath = "/api/v1/browser-session";
    private const string RatePolicy = "browser-session";

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
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Bounded total state; no account identifier or secret appears in a limiter key.
            options.AddFixedWindowLimiter(RatePolicy, limiter =>
            { limiter.PermitLimit = 60; limiter.Window = TimeSpan.FromMinutes(1); limiter.QueueLimit = 0; });
        });
    }

    public static void MapBrowserSessions(this WebApplication app)
    {
        var group = app.MapGroup(CookiePath).RequireRateLimiting(RatePolicy);
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
