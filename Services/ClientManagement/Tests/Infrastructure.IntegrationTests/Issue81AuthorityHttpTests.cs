using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "81")]
public sealed class Issue81AuthorityHttpTests
{
    [Fact]
    public async Task Eligibility_retries_one_transient_response_and_validates_exact_echo_and_service_token()
    {
        var organisation = Guid.NewGuid(); var subject = Guid.NewGuid();
        var actor = Guid.NewGuid(); var target = Guid.NewGuid();
        var calls = 0;
        using var provider = Provider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/internal/v1/assignee-eligibility", request.RequestUri!.AbsolutePath);
            Assert.Equal(new AuthenticationHeaderValue("Bearer", "synthetic-service-token"),
                request.Headers.Authorization);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains(target.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
            calls++;
            return calls == 1 ? Response(HttpStatusCode.ServiceUnavailable, "{}")
                : Response(HttpStatusCode.OK, $$"""
                    {"contractVersion":1,"organisationId":"{{organisation}}","actorMembershipId":"{{actor}}",
                     "targetMembershipId":"{{target}}","eligible":true,"targetViewScope":"All",
                     "decisionVersion":"v1","observedAtUtc":"{{DateTimeOffset.UtcNow:O}}"}
                    """);
        });
        var decision = await provider.GetRequiredService<IActionAuthorityReads>()
            .EligibilityAsync(organisation, subject, actor, target, default);
        Assert.Equal("All", decision?.TargetViewScope);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Eligibility_does_not_retry_malformed_or_mismatched_authority()
    {
        var calls = 0;
        using var provider = Provider(_ =>
        {
            calls++;
            return Task.FromResult(Response(HttpStatusCode.OK,
                "{\"contractVersion\":1,\"eligible\":true,\"targetViewScope\":\"All\"}"));
        });
        var read = provider.GetRequiredService<IActionAuthorityReads>();
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => read.EligibilityAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Eligibility_missing_explicit_eligible_field_is_unavailable_not_semantic_denial()
    {
        using var provider = Provider(_ => Task.FromResult(Response(HttpStatusCode.OK,
            "{\"contractVersion\":1}")));
        var error = await Assert.ThrowsAsync<TenantAccessException>(() =>
            provider.GetRequiredService<IActionAuthorityReads>().EligibilityAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
    }

    [Fact]
    public async Task Eligibility_semantic_false_and_dependency_auth_are_distinct_fail_closed_results()
    {
        using var falseProvider = Provider(_ => Task.FromResult(Response(HttpStatusCode.OK,
            "{\"contractVersion\":1,\"eligible\":false}")));
        Assert.Null(await falseProvider.GetRequiredService<IActionAuthorityReads>().EligibilityAsync(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        using var deniedProvider = Provider(_ => Task.FromResult(Response(HttpStatusCode.Unauthorized, "{}")));
        await Assert.ThrowsAsync<TenantAccessException>(() => deniedProvider.GetRequiredService<IActionAuthorityReads>()
            .EligibilityAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Eligibility_two_second_total_deadline_cancels_without_a_write_decision()
    {
        var calls = 0;
        using var provider = Provider(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(HttpStatusCode.OK, "{}");
        });
        var watch = Stopwatch.StartNew();
        var denied = await Assert.ThrowsAsync<TenantAccessException>(() =>
            provider.GetRequiredService<IActionAuthorityReads>().EligibilityAsync(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default));
        watch.Stop();
        Assert.Equal(AccessFailure.Unavailable, denied.Failure);
        Assert.Equal(1, calls);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 2.8);
    }

    [Fact]
    public async Task Timezone_accepts_exact_canonical_IANA_and_rejects_missing_or_mismatched_configuration()
    {
        var organisation = Guid.NewGuid();
        using var good = Provider(_ => Task.FromResult(Response(HttpStatusCode.OK, $$"""
            {"contractVersion":1,"organisationId":"{{organisation}}","timeZoneId":"Europe/Brussels",
             "configVersion":1,"observedAtUtc":"{{DateTimeOffset.UtcNow:O}}"}
            """)));
        Assert.Equal("Europe/Brussels", (await good.GetRequiredService<IActionAuthorityReads>()
            .TimeZoneAsync(organisation, Guid.NewGuid(), Guid.NewGuid(), default)).TimeZoneId);
        using var missing = Provider(_ => Task.FromResult(Response(HttpStatusCode.NotFound, "{}")));
        await Assert.ThrowsAsync<TenantAccessException>(() => missing.GetRequiredService<IActionAuthorityReads>()
            .TimeZoneAsync(organisation, Guid.NewGuid(), Guid.NewGuid(), default));
        using var wrong = Provider(_ => Task.FromResult(Response(HttpStatusCode.OK, $$"""
            {"contractVersion":1,"organisationId":"{{Guid.NewGuid()}}","timeZoneId":"Europe/Brussels",
             "configVersion":1,"observedAtUtc":"{{DateTimeOffset.UtcNow:O}}"}
            """)));
        await Assert.ThrowsAsync<TenantAccessException>(() => wrong.GetRequiredService<IActionAuthorityReads>()
            .TimeZoneAsync(organisation, Guid.NewGuid(), Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData("2026-03-29T00:30:00+00:00", 29)]
    [InlineData("2026-03-29T01:30:00+00:00", 29)]
    [InlineData("2026-10-25T00:30:00+00:00", 25)]
    [InlineData("2026-10-25T01:30:00+00:00", 25)]
    public void Brussels_spring_and_fall_DST_have_one_unambiguous_organisation_calendar_day(
        string instant, int expectedDay)
    {
        var utc = DateTimeOffset.Parse(instant);
        var scope = new ActionWriteAuthorityScope();
        scope.SetTimeZone(new ActionTimeZone("Europe/Brussels", 1, utc,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels")), utc);
        scope.SetAssignee(Guid.NewGuid(), new AssigneeDecision("All", "v1", utc));
        Assert.Equal(expectedDay, scope.LocalDay?.Day);
        scope.ValidateFinal(utc);
    }

    [Theory]
    [InlineData("2026-03-28T22:59:59+00:00")]
    [InlineData("2026-10-24T21:59:59+00:00")]
    public void Final_guard_rejects_Brussels_midnight_even_when_observations_are_two_seconds_old(
        string beforeMidnight)
    {
        var observed = DateTimeOffset.Parse(beforeMidnight);
        var scope = new ActionWriteAuthorityScope();
        scope.SetTimeZone(new ActionTimeZone("Europe/Brussels", 1, observed,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels")), observed);
        scope.SetAssignee(Guid.NewGuid(), new AssigneeDecision("All", "v1", observed));
        var denied = Assert.Throws<TenantAccessException>(() => scope.ValidateFinal(observed.AddSeconds(2)));
        Assert.Equal(AccessFailure.Unavailable, denied.Failure);
    }

    private static ServiceProvider Provider(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
        => Provider((request, _) => send(request));

    private static ServiceProvider Provider(Func<HttpRequestMessage, CancellationToken,
        Task<HttpResponseMessage>> send)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = "Host=localhost;Database=unused;Username=unused",
            ["ActionAuthority:AuthManagementUrl"] = "https://auth.synthetic.invalid/"
        });
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddInfrastructure(config);
        services.RemoveAll<IActionAuthorityServiceCredential>();
        services.AddSingleton<IActionAuthorityServiceCredential>(new Token());
        services.AddHttpClient("ActionAuthority").ConfigurePrimaryHttpMessageHandler(() => new Handler(send));
        return services.BuildServiceProvider();
    }

    private sealed class Token : IActionAuthorityServiceCredential
    {
        public string? BearerToken => "synthetic-service-token";
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken,
        Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
