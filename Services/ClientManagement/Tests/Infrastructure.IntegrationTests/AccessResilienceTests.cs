using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using ClientManagement.API.Services;
using Microsoft.AspNetCore.Http;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class AccessResilienceTests
{
    [Theory]
    [InlineData(401)] [InlineData(403)] [InlineData(503)]
    public async Task Target_membership_adapter_also_preserves_authentication_failure(int status)
    {
        using var http = Client(new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        var adapter = new StaffMembershipClient(http, new Credential());
        if (status == 403) Assert.False(await adapter.VerifyAsync(Guid.NewGuid(), Guid.NewGuid(), true, default));
        else
        {
            var exception = await Assert.ThrowsAsync<TenantAccessException>(() => adapter.VerifyAsync(Guid.NewGuid(), Guid.NewGuid(), true, default));
            Assert.Equal(status == 401 ? AccessFailure.Unauthenticated : AccessFailure.Unavailable, exception.Failure);
        }
    }

    [Theory]
    [InlineData(401, 401, 1)] [InlineData(403, 403, 1)]
    [InlineData(503, 503, 2)] [InlineData(500, 503, 2)] [InlineData(429, 503, 2)]
    public async Task Authority_outcome_survives_application_and_http_without_establishing_context(int upstream, int downstream, int calls)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)upstream)));
        using var http = Client(handler);
        var tenant = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity(Guid.NewGuid()), tenant, tenant);
        var context = new DefaultHttpContext();
        await new TenantAccessMiddleware(async _ => await operation.AuthorizeAsync(new("ReferenceData.View"), default)).InvokeAsync(context);
        Assert.Equal(downstream, context.Response.StatusCode);
        Assert.Equal(calls, handler.Calls);
        Assert.ThrowsAny<InvalidOperationException>(() => tenant.Current);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Safe_read_retries_once_and_recovers_without_caching(bool transport)
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler((attempt, _) => attempt == 1
            ? transport ? Task.FromException<HttpResponseMessage>(new HttpRequestException("sensitive exception"))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            : Task.FromResult(Allowed(organisation)));
        using var http = Client(handler);
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        Assert.Equal(TenantAccessOutcome.Authorized, (await adapter.ResolveAsync("subject", organisation, default)).Outcome);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(TenantAccessOutcome.Authorized, (await adapter.ResolveAsync("subject", organisation, default)).Outcome);
        Assert.Equal(3, handler.Calls);
    }

    [Theory]
    [InlineData("json")] [InlineData("empty")] [InlineData("version")] [InlineData("permission")]
    [InlineData("duplicate")] [InlineData("membership")] [InlineData("decision")] [InlineData("observation")]
    public async Task Malformed_contracts_are_unavailable_and_not_retried(string invalid)
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler((_, _) => Task.FromResult(invalid == "json"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{sensitive invalid body") }
            : invalid == "empty" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("null") }
            : Allowed(organisation, invalid)));
        using var http = Client(handler);
        Assert.Equal(TenantAccessOutcome.Unavailable,
            (await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", organisation, default)).Outcome);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retry_shares_timeout_budget_and_caller_cancellation_propagates()
    {
        var handler = new Handler(async (attempt, token) =>
        {
            if (attempt == 1) { await Task.Delay(500, token); return new(HttpStatusCode.ServiceUnavailable); }
            await Task.Delay(Timeout.Infinite, token); return new(HttpStatusCode.OK);
        });
        using var http = Client(handler);
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        var watch = Stopwatch.StartNew();
        Assert.Equal(TenantAccessOutcome.Unavailable, (await adapter.ResolveAsync("subject", Guid.NewGuid(), default)).Outcome);
        Assert.Equal(2, handler.Calls);
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.5, 3);
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.ResolveAsync("subject", Guid.NewGuid(), cancellation.Token));
    }

    [Fact]
    public async Task Missing_credential_is_unauthenticated_without_an_http_call()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException());
        using var http = Client(handler);
        Assert.Equal(TenantAccessOutcome.Unauthenticated,
            (await new CurrentTenantAccessClient(http, new Credential(null)).ResolveAsync("subject", Guid.NewGuid(), default)).Outcome);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Metrics_and_traces_record_retry_latency_and_failure_without_identity_or_body()
    {
        var tags = new List<KeyValuePair<string, object?>>();
        var instruments = new HashSet<string>();
        Activity? completed = null;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        { if (instrument.Meter.Name == CurrentTenantAccessClient.DiagnosticsName) meter.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((instrument, value, values, _) =>
        { lock (tags) { instruments.Add(instrument.Name); tags.AddRange(values.ToArray()); } });
        listener.SetMeasurementEventCallback<double>((instrument, value, values, _) =>
        { lock (tags) { instruments.Add(instrument.Name); tags.AddRange(values.ToArray()); } });
        listener.Start();
        using var traces = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CurrentTenantAccessClient.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => completed = activity
        };
        ActivitySource.AddActivityListener(traces);
        using var http = Client(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("private-response-body") })));
        await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", Guid.NewGuid(), default);
        Assert.Contains("tenant_access.attempts", instruments);
        Assert.Contains("tenant_access.authority_failures", instruments);
        Assert.Contains("tenant_access.duration", instruments);
        Assert.NotNull(completed);
        Assert.Equal("Unavailable", completed!.GetTagItem("outcome"));
        Assert.Equal(2, completed.GetTagItem("attempt.count"));
        tags.AddRange(completed.TagObjects);
        Assert.All(tags, tag => Assert.Contains(tag.Key, new[] { "attempt", "attempt.count", "reason", "outcome", "contract.version" }));
        Assert.DoesNotContain(tags, tag => (tag.Value?.ToString() ?? "").Contains("private", StringComparison.Ordinal));
    }

    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://auth.invalid/") };
    private sealed record Credential(string? BearerToken = "synthetic-test-token") : ITenantAccessCredential;
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "subject"; }
    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("synthetic-test-token", request.Headers.Authorization?.Parameter);
            return response(++Calls, token);
        }
    }
    private static HttpResponseMessage Allowed(Guid organisation, string invalid = "") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            ContractVersion = invalid == "version" ? 2 : 1, SubjectId = "subject", OrganisationId = organisation,
            OrganisationMembershipId = invalid == "membership" ? Guid.Empty : Guid.NewGuid(),
            EffectivePermissionCodes = invalid == "duplicate" ? new[] { "ReferenceData.View", "ReferenceData.View" }
                : new[] { invalid == "permission" ? "unknown" : "ReferenceData.View" },
            DecisionVersion = invalid == "decision" ? "" : "1",
            ObservedAtUtc = invalid == "observation" ? default : DateTimeOffset.UtcNow
        })
    };
}
