using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using AuthManager.API.Endpoints;
using AuthManager.Application.Onboarding;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// These invoke real transport functions in-process, not HTTP or Origin/CSRF middleware acceptance.
public sealed class BrowserRegistrationBoundaryTests
{
    [Fact]
    public async Task Declared_oversized_body_is_refused_before_the_stream_is_read()
    {
        var body = new FailingBody(new IOException("body-must-not-be-read"));
        var http = new DefaultHttpContext();
        http.Request.ContentLength = 8193;
        http.Request.Body = body;
        Assert.Null(await ReadAsync(http));
        Assert.Equal(0, body.Reads);
    }

    [Fact]
    public async Task Unsized_chunked_body_is_bounded_and_the_exact_limit_still_parses()
    {
        const string json = "{\"email\":\"unit@example.invalid\"}";
        var http = new DefaultHttpContext();
        http.Request.Body = new ChunkedBody(Encoding.UTF8.GetBytes(json + new string(' ', 8192 - json.Length)));
        Assert.Equal("unit@example.invalid", (await ReadAsync(http))!.Email);
        http.Request.Body = new ChunkedBody(Encoding.UTF8.GetBytes(json + new string(' ', 8193 - json.Length)));
        Assert.Null(await ReadAsync(http));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"extra\":{\"a\":{\"b\":{\"c\":{\"d\":1}}}}}")]
    public async Task Malformed_or_overdeep_json_cannot_become_an_admitted_request(string json)
    {
        var http = Context(json);
        Assert.Null(await ReadAsync(http));
    }

    [Fact]
    public async Task Bad_HTTP_body_errors_are_safe_refusals_but_transport_IO_errors_remain_visible()
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new FailingBody(new BadHttpRequestException("synthetic-invalid-body"));
        Assert.Null(await ReadAsync(http));
        var failure = new IOException("synthetic-body-transport-failure");
        http.Request.Body = new FailingBody(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => ReadAsync(http)));
    }

    [Fact]
    public async Task Aborted_body_reads_preserve_cancellation_instead_of_becoming_invalid_JSON()
    {
        using var aborted = new CancellationTokenSource();
        aborted.Cancel();
        var http = new DefaultHttpContext { RequestAborted = aborted.Token };
        http.Request.Body = new FailingBody(new OperationCanceledException(aborted.Token));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(http));
        Assert.Equal(aborted.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData("ResendAsync", "{}")]
    [InlineData("ResendAsync", "{\"email\":null}")]
    [InlineData("ExchangeAsync", "{}")]
    [InlineData("ExchangeAsync", "{\"token\":null}")]
    public async Task Missing_required_fields_are_refused_before_service_resolution(string method, string json)
    {
        var http = Context(json);
        var result = await EndpointAsync(method, http);
        Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Real_registration_validation_refuses_bad_addresses_and_noncanonical_challenges()
    {
        using var registration = new RegistrationServiceInputs();
        using var services = new ServiceCollection().AddSingleton<IRegistrationService>(registration.Service).BuildServiceProvider();
        foreach (var input in new[]
        {
            ("RegisterAsync", "{\"email\":\"invalid\"}"),
            ("ResendAsync", "{\"email\":\"invalid\"}"),
            ("ExchangeAsync", "{\"token\":\"malformed\"}")
        })
        {
            var http = Context(input.Item2);
            http.RequestServices = services;
            Assert.Equal(400, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await EndpointAsync(input.Item1, http)).StatusCode);
            Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
        }
        registration.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public async Task Resend_requires_an_observed_peer_and_live_registration_readiness()
    {
        using var registration = new RegistrationServiceInputs();
        using var services = new ServiceCollection().AddSingleton<IRegistrationService>(registration.Service)
            .AddSingleton(new BrowserRegistrationEndpoints.RegistrationAbuseLimits(new RegistrationStore.Clock())).BuildServiceProvider();
        var noPeer = Context("{\"email\":\"unit@example.invalid\"}");
        noPeer.RequestServices = services;
        Assert.Equal(429, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await EndpointAsync("ResendAsync", noPeer)).StatusCode);
        var unready = Context("{\"email\":\"unit@example.invalid\"}");
        unready.RequestServices = services;
        unready.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.Equal(503, Assert.IsAssignableFrom<IStatusCodeHttpResult>(await EndpointAsync("ResendAsync", unready)).StatusCode);
        Assert.False(unready.Response.Headers.ContainsKey("Set-Cookie"));
        registration.AssertNoIdentityOrHashAccess();
    }

    [Fact]
    public void IPv4_mapped_addresses_share_the_actual_IP_quota()
    {
        using var limits = new BrowserRegistrationEndpoints.RegistrationAbuseLimits(new RegistrationStore.Clock());
        using var services = new ServiceCollection().AddSingleton(limits).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        Assert.False(Invoke<bool>("Admitted", http, "address", false));
        for (var index = 0; index < 10; index++)
        {
            http.Connection.RemoteIpAddress = index < 5 ? IPAddress.Loopback : IPAddress.Parse("::ffff:127.0.0.1");
            Assert.True(Invoke<bool>("Admitted", http, "address-" + index, false));
        }
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.False(Invoke<bool>("Admitted", http, "eleventh-address", false));
    }

    [Fact]
    public void Capacity_refuses_new_partitions_without_erasing_existing_limits_then_reclaims_expired_entries()
    {
        var clock = new RegistrationStore.Clock();
        using var limits = new BrowserRegistrationEndpoints.RegistrationAbuseLimits(clock);
        for (var index = 0; index < 4096; index++)
            Assert.True(limits.TryAcquire("unit-ip-" + index, "unit-address-" + index, false));
        Assert.False(limits.TryAcquire("new-ip", "new-address", false));
        Assert.False(limits.TryAcquire("unit-ip-0", "new-address", false));
        Assert.False(limits.TryAcquire("new-ip", "unit-address-0", false));
        Assert.True(limits.TryAcquire("unit-ip-0", "unit-address-0", false));
        clock.UtcNow = clock.UtcNow.AddHours(1);
        Assert.True(limits.TryAcquire("new-ip", "new-address", false));
    }

    [Fact]
    public void Exchange_and_registration_budgets_are_separate_and_expire_at_their_own_boundaries()
    {
        var clock = new RegistrationStore.Clock();
        using var limits = new BrowserRegistrationEndpoints.RegistrationAbuseLimits(clock);
        for (var index = 0; index < 5; index++) Assert.True(limits.TryAcquire("ip", "subject", false));
        Assert.False(limits.TryAcquire("ip", "subject", false));
        for (var index = 0; index < 5; index++) Assert.True(limits.TryAcquire("ip", "subject", true));
        Assert.False(limits.TryAcquire("ip", "subject", true));
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        Assert.True(limits.TryAcquire("ip", "subject", true));
        Assert.False(limits.TryAcquire("ip", "subject", false));
        clock.UtcNow = clock.UtcNow.AddMinutes(59);
        Assert.True(limits.TryAcquire("ip", "subject", false));
    }

    [Theory]
    [InlineData(RegistrationOutcome.Unavailable, false, false, 503)]
    [InlineData(RegistrationOutcome.Invalid, false, false, 400)]
    [InlineData(RegistrationOutcome.Accepted, false, true, 503)]
    [InlineData(RegistrationOutcome.Accepted, true, false, 503)]
    public void Incomplete_or_rejected_exchange_never_emits_an_onboarding_cookie(RegistrationOutcome outcome,
        bool credential, bool expiry, int expected)
    {
        var http = new DefaultHttpContext();
        var result = Invoke<IResult>("CompleteExchange", http, new VerificationExchange
        {
            Outcome = outcome, Credential = credential ? RegistrationServiceInputs.NewCredential() : null,
            ExpiresAtUtc = expiry ? RegistrationStore.Now.AddMinutes(10) : null
        }, CancellationToken.None);
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public void Cookie_writer_preserves_the_narrow_onboarding_transport_contract()
    {
        // This synthetic DTO tests only cookie serialization, not authentication or a genuine session.
        var http = new DefaultHttpContext();
        var credential = RegistrationServiceInputs.NewCredential();
        var expiry = RegistrationStore.Now.AddMinutes(10);
        var result = Invoke<IResult>("CompleteExchange", http, new VerificationExchange
        {
            Outcome = RegistrationOutcome.Accepted, Credential = credential, ExpiresAtUtc = expiry
        }, CancellationToken.None);
        Assert.Equal(204, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var header = Assert.Single(http.Response.Headers.SetCookie);
        var cookie = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(header);
        Assert.Equal("__Secure-ZekaOnboarding", cookie.Name.Value);
        Assert.True(cookie.Value.Value == credential);
        Assert.Equal("/api/v1/browser-session/onboarding", cookie.Path.Value);
        Assert.Equal("Strict", cookie.SameSite.ToString());
        Assert.True(cookie.Secure);
        Assert.True(cookie.HttpOnly);
        Assert.False(cookie.Domain.HasValue);
        Assert.Equal(expiry, cookie.Expires);
    }

    [Fact]
    public void Cancelled_completion_cannot_emit_even_a_synthetic_onboarding_cookie()
    {
        var http = new DefaultHttpContext();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var error = Assert.ThrowsAny<OperationCanceledException>(() => Invoke<IResult>("CompleteExchange", http,
            new VerificationExchange
            {
                Outcome = RegistrationOutcome.Accepted, Credential = RegistrationServiceInputs.NewCredential(),
                ExpiresAtUtc = RegistrationStore.Now.AddMinutes(10)
            }, cancelled.Token));
        Assert.Equal(cancelled.Token, error.CancellationToken);
        Assert.False(http.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Deadline_returns_unavailable_without_completing_an_unconfirmed_operation()
    {
        using var host = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = host.Token };
        var completed = false;
        var observed = CancellationToken.None;
        try
        {
            var result = await DeadlineAsync(http, async token =>
            {
                observed = token;
                await Task.Delay(Timeout.Infinite, token);
                return 1;
            }, (_, _) => { completed = true; return 200; }, 503).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(503, result);
            Assert.True(observed.IsCancellationRequested);
            Assert.False(host.IsCancellationRequested);
            Assert.False(completed);
        }
        finally { host.Cancel(); }
    }

    [Fact]
    public async Task Completion_after_host_cancellation_cannot_continue_the_transport_result()
    {
        using var host = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = host.Token };
        var completed = false;
        var result = await DeadlineAsync(http, token => { host.Cancel(); return Task.FromResult(1); },
            (_, _) => { completed = true; return 200; }, 503);
        Assert.Equal(503, result);
        Assert.False(completed);
    }

    [Fact]
    public async Task Caller_cancellation_and_unexplained_cancellation_are_not_reclassified_as_deadlines()
    {
        using var host = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = host.Token };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeadlineAsync(http,
            token => { host.Cancel(); return Task.FromCanceled<int>(token); }, (value, _) => value, -1));
        http.RequestAborted = CancellationToken.None;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DeadlineAsync(http,
            _ => throw new OperationCanceledException(), (value, _) => value, -1));
    }

    [Fact]
    public async Task Only_a_normally_completed_operation_runs_the_completion_once_with_a_live_linked_token()
    {
        var http = new DefaultHttpContext();
        var completions = 0;
        var result = await DeadlineAsync(http, token =>
        {
            Assert.True(token.CanBeCanceled);
            return Task.FromResult(7);
        }, (value, token) =>
        {
            Assert.False(token.IsCancellationRequested);
            completions++;
            return value;
        }, -1);
        Assert.Equal(7, result);
        Assert.Equal(1, completions);
    }

    private static DefaultHttpContext Context(string json)
    {
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return http;
    }
    private static Task<RegistrationRequest?> ReadAsync(HttpContext http)
        => (Task<RegistrationRequest?>)Method("ReadBodyAsync").MakeGenericMethod(typeof(RegistrationRequest)).Invoke(null, [http])!;
    private static Task<IResult> EndpointAsync(string name, HttpContext http) => Invoke<Task<IResult>>(name, http);
    private static Task<int> DeadlineAsync(HttpContext http, Func<CancellationToken, Task<int>> work,
        Func<int, CancellationToken, int> complete, int unavailable)
    {
        var method = typeof(BrowserRegistrationEndpoints).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(item => item.Name == "WithinDeadline" && item.GetGenericArguments().Length == 2);
        return (Task<int>)method.MakeGenericMethod(typeof(int), typeof(int)).Invoke(null, [http, work, complete, unavailable])!;
    }
    private static MethodInfo Method(string name) => typeof(BrowserRegistrationEndpoints).GetMethod(name,
        BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException("PRODUCTION_TRANSPORT_METHOD_MISSING");
    private static T Invoke<T>(string name, params object[] arguments)
    {
        try { return (T)Method(name).Invoke(null, arguments)!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private sealed class ChunkedBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(17, buffer.Length)], cancellationToken);
    }
    private sealed class FailingBody(Exception error) : MemoryStream
    {
        public int Reads { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reads++; return ValueTask.FromException<int>(error); }
    }
}
