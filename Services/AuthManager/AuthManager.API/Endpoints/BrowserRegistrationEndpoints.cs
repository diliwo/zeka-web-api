using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Onboarding;
using AuthManager.Infrastructure.Onboarding;

[assembly: InternalsVisibleTo("Infrastructure.IntegrationTests")]

namespace AuthManager.API.Endpoints;

internal static class BrowserRegistrationEndpoints
{
    private const string AcceptedMessage = "If the address can be registered, verification instructions will be sent.";
    private const string InvalidMessage = "The registration request is invalid.";
    private const string InvalidVerification = "The verification request is invalid.";
    private const string OnboardingCookie = "__Secure-ZekaOnboarding";
    private const int MaximumBodyBytes = 8192;

    public static void MapBrowserRegistrations(this RouteGroupBuilder sessions)
    {
        var registrations = sessions.MapGroup("/onboarding/registrations");
        registrations.MapPost("", (Delegate)RegisterAsync).AllowAnonymous();
        registrations.MapPost("/resend", (Delegate)ResendAsync).AllowAnonymous();
        registrations.MapPost("/email-verification/exchange", (Delegate)ExchangeAsync).AllowAnonymous();
    }

    private static async Task<IResult> RegisterAsync(HttpContext http)
    {
        var start = Stopwatch.GetTimestamp();
        var input = await ReadBodyAsync<RegistrationRequest>(http);
        if (input is null) return Invalid(InvalidMessage);
        var service = http.RequestServices.GetRequiredService<IRegistrationService>();
        var address = service.NormalizeAddress(input.Email);
        if (address is null) return Invalid(InvalidMessage);
        if (!Admitted(http, address, exchange: false)) return Results.StatusCode(429);
        var status = RegistrationReady(http)
            ? await WithinDeadline(http, ct => service.RegisterAsync(input, ct), RegistrationOutcome.Unavailable)
            : RegistrationOutcome.Unavailable;
        await PadAsync(start, http.RequestAborted);
        return RegistrationResponse(status);
    }

    private static async Task<IResult> ResendAsync(HttpContext http)
    {
        var start = Stopwatch.GetTimestamp();
        var input = await ReadBodyAsync<ResendVerificationRequest>(http);
        if (input?.Email is null) return Invalid(InvalidMessage);
        var service = http.RequestServices.GetRequiredService<IRegistrationService>();
        var address = service.NormalizeAddress(input.Email);
        if (address is null) return Invalid(InvalidMessage);
        if (!Admitted(http, address, exchange: false)) return Results.StatusCode(429);
        var status = RegistrationReady(http)
            ? await WithinDeadline(http, ct => service.ResendAsync(input.Email, ct), RegistrationOutcome.Unavailable)
            : RegistrationOutcome.Unavailable;
        await PadAsync(start, http.RequestAborted);
        return RegistrationResponse(status);
    }

    private static async Task<IResult> ExchangeAsync(HttpContext http)
    {
        var input = await ReadBodyAsync<ExchangeVerificationRequest>(http);
        if (input?.Token is null) return Invalid(InvalidVerification);
        var service = http.RequestServices.GetRequiredService<IRegistrationService>();
        if (service.ReadChallengeId(input.Token) is not { } challenge) return Invalid(InvalidVerification);
        if (!Admitted(http, challenge.ToString("N"), exchange: true)) return Results.StatusCode(429);
        if (!RegistrationReady(http)) return Results.StatusCode(503);

        return await WithinDeadline(http, ct => service.ExchangeAsync(input.Token, ct),
            (result, ct) => CompleteExchange(http, result, ct), Results.StatusCode(503));
    }

    private static IResult CompleteExchange(HttpContext http, VerificationExchange result, CancellationToken cancellationToken)
    {
        if (result.Outcome == RegistrationOutcome.Unavailable) return Results.StatusCode(503);
        if (result.Outcome != RegistrationOutcome.Accepted) return Invalid(InvalidVerification);
        if (result.Credential is null || result.ExpiresAtUtc is null) return Results.StatusCode(503);

        var cookie = new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, IsEssential = true,
            Path = BrowserSessionEndpoints.CookiePath + "/onboarding", Expires = result.ExpiresAtUtc
        };
        cancellationToken.ThrowIfCancellationRequested();
        http.Response.Cookies.Append(OnboardingCookie, result.Credential, cookie);
        return Results.NoContent();
    }

    private static bool RegistrationReady(HttpContext http)
        => http.RequestServices.GetService<RegistrationReadiness>()?.IsReady == true;

    private static bool Admitted(HttpContext http, string partition, bool exchange)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return http.RequestServices.GetRequiredService<RegistrationAbuseLimits>()
            .TryAcquire(address.ToString(), partition, exchange);
    }

    private static IResult RegistrationResponse(RegistrationOutcome status) => status switch
    {
        RegistrationOutcome.Accepted => Results.Json(new { message = AcceptedMessage }, statusCode: 202),
        RegistrationOutcome.Invalid => Invalid(InvalidMessage),
        _ => Results.StatusCode(503)
    };

    private static IResult Invalid(string message) => Results.Json(new { message }, statusCode: 400);

    private static async Task<T?> ReadBodyAsync<T>(HttpContext http) where T : class
    {
        if (http.Request.ContentLength > MaximumBodyBytes) return null;
        try
        {
            // Read a capped body after the parent group has enforced Origin and antiforgery.
            var buffer = new byte[MaximumBodyBytes + 1];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await http.Request.Body.ReadAsync(buffer.AsMemory(used), http.RequestAborted);
                if (read == 0)
                    return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, used), JsonOptions);
                used += read;
            }
            return null;
        }
        catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };

    private static Task<T> WithinDeadline<T>(HttpContext http, Func<CancellationToken, Task<T>> request, T unavailable)
        => WithinDeadline(http, request, (result, _) => result, unavailable);

    private static async Task<TResult> WithinDeadline<TWork, TResult>(HttpContext http,
        Func<CancellationToken, Task<TWork>> request, Func<TWork, CancellationToken, TResult> complete, TResult unavailable)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            var result = await request(deadline.Token);
            return deadline.IsCancellationRequested ? unavailable : complete(result, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            return unavailable;
        }
    }

    private static Task PadAsync(long start, CancellationToken ct)
    {
        var minimum = TimeSpan.FromMilliseconds(500 + RandomNumberGenerator.GetInt32(26));
        var wait = minimum - Stopwatch.GetElapsedTime(start);
        return wait > TimeSpan.Zero ? Task.Delay(wait, ct) : Task.CompletedTask;
    }

    internal sealed class RegistrationAbuseLimits(TimeProvider clock) : IDisposable
    {
        private const int Capacity = 8192;
        private readonly byte[] quotaKey = RandomNumberGenerator.GetBytes(32);
        private readonly Dictionary<string, (int Count, DateTimeOffset Until)> buckets = new(StringComparer.Ordinal);
        private readonly object mutex = new();
        private DateTimeOffset nextTrim;

        public bool TryAcquire(string ip, string partition, bool exchange)
        {
            var ipKey = Digest(exchange ? "exchange/ip" : "registration/ip", ip);
            var subjectKey = Digest(exchange ? "exchange/challenge" : "registration/address", partition);
            lock (mutex)
            {
                var now = clock.GetUtcNow();
                if (now >= nextTrim || buckets.Count >= Capacity - 1)
                {
                    foreach (var entry in buckets.Where(e => e.Value.Until <= now).Select(e => e.Key).ToArray())
                        buckets.Remove(entry);
                    nextTrim = now.AddMinutes(1);
                }

                var ipBudget = GetBudget(ipKey, now, exchange ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(10));
                var subjectBudget = GetBudget(subjectKey, now, exchange ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1));
                if (ipBudget.Count >= 10 || subjectBudget.Count >= 5
                    || buckets.Count + (buckets.ContainsKey(ipKey) ? 0 : 1) + (buckets.ContainsKey(subjectKey) ? 0 : 1) > Capacity)
                    return false;
                buckets[ipKey] = (ipBudget.Count + 1, ipBudget.Until);
                buckets[subjectKey] = (subjectBudget.Count + 1, subjectBudget.Until);
                return true;
            }
        }

        private (int Count, DateTimeOffset Until) GetBudget(string key, DateTimeOffset now, TimeSpan window)
            => buckets.TryGetValue(key, out var existing) && existing.Until > now ? existing : (0, now.Add(window));

        private string Digest(string kind, string value)
            => Convert.ToHexString(HMACSHA256.HashData(quotaKey, Encoding.UTF8.GetBytes(kind + "\0" + value)));

        public void Dispose() => CryptographicOperations.ZeroMemory(quotaKey);
    }
}
