using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Infrastructure.Authorization;
using AdminAreaManagement.API.Services;
using Microsoft.AspNetCore.Http;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "17")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class AuthorityResponseCompatibilityTests
{
    [Fact]
    public async Task Owner_response_extra_grants_are_ignored_and_cannot_authorize_a_checked_permission()
    {
        var org = Guid.NewGuid();
        using var http = Http(new Handler(_ => Allowed(org, permissions:
            ["ReferenceData.View", "Organisations.Export", "Organisations.Close", "Future.Permission", "referencedata.View"])));
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        var decision = await adapter.ResolveAsync("subject", org, default);
        Assert.Equal(TenantAccessOutcome.Authorized, decision.Outcome);
        Assert.Equal(new[] { "ReferenceData.View" }, decision.Membership!.EffectivePermissionCodes);
        var scope = new TenantContextScope();
        var operation = new TenantOperation(adapter, new Identity("subject", org), scope, scope);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        var denied = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("Future.Permission"), default));
        Assert.Equal(AccessFailure.Denied, denied.Failure);
        Assert.Equal(TenantAccessDiagnostics.PermissionCheckUnknown, denied.Reason);
    }

    [Theory]
    [InlineData("json")] [InlineData("null")] [InlineData("version")]
    [InlineData("permissions_null")] [InlineData("duplicate")] [InlineData("permission_empty")]
    [InlineData("permission_whitespace")] [InlineData("permission_control")]
    [InlineData("permission_segment")] [InlineData("decision")] [InlineData("observation")]
    [InlineData("offset")] [InlineData("quoted_version")] [InlineData("root_array")]
    [InlineData("duplicate_json")] [InlineData("duplicate_json_case")] [InlineData("missing_membership")]
    public async Task Malformed_success_is_named_unavailable_without_retry(string malformed)
    {
        var org = Guid.NewGuid(); var calls = 0;
        using var http = Http(new Handler(_ => { calls++; return Allowed(org, malformed); }));
        var decision = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", org, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
        Assert.Equal(TenantAccessDiagnostics.ResponseMalformed, decision.Reason);
        Assert.Null(decision.Membership);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("subject", "subject_mismatch")]
    [InlineData("organisation", "organisation_mismatch")]
    [InlineData("membership", "contradictory_response")]
    public async Task Successful_identity_contradiction_refuses_and_emits_only_allowlisted_operational_event(string invalid, string mismatch)
    {
        var org = Guid.NewGuid();
        var events = new List<ActivityEvent>();
        using var listener = Listen(events);
        using var http = Http(new Handler(_ => Allowed(org, invalid)));
        var decision = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", org, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, decision.Reason);
        Assert.Null(decision.Membership);
        var signal = Assert.Single(events.Where(e => e.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal(TimeSpan.Zero, signal.Timestamp.Offset);
        Assert.NotEqual(default, signal.Timestamp);
        var tags = signal.Tags.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(new[] { "contract_version", "event_code", "mismatch_class", "service" }, tags.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, tags["event_code"]);
        Assert.Equal("AdminAreaManagement", tags["service"]);
        Assert.Equal(mismatch, tags["mismatch_class"]);
        Assert.Equal(1, tags["contract_version"]);
        var safe = JsonSerializer.Serialize(tags);
        Assert.DoesNotContain("subject", safe.Replace("subject_mismatch", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(org.ToString(), safe, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-test-token", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceData.View", safe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telemetry_export_failure_cannot_turn_integrity_refusal_into_authorization()
    {
        using var scope = new Activity("admin-exporter-failure-conformance").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CurrentTenantAccessClient.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == scope.TraceId) throw new InvalidOperationException("private-exporter-error");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var org = Guid.NewGuid();
        using var http = Http(new Handler(_ => Allowed(org, "subject")));
        var decision = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("subject", org, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, decision.Reason);
        Assert.Null(decision.Membership);
    }

    [Theory]
    [InlineData(401, TenantAccessOutcome.Unauthenticated, 401, null)]
    [InlineData(403, TenantAccessOutcome.Denied, 403, null)]
    [InlineData(503, TenantAccessOutcome.Unavailable, 503, TenantAccessDiagnostics.DependencyUnavailable)]
    public async Task Explicit_upstream_outcomes_keep_http_classification(int status, TenantAccessOutcome expected, int downstream, string? reason)
    {
        var org = Guid.NewGuid();
        using var http = Http(new Handler(_ => new((HttpStatusCode)status)));
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        var result = await adapter.ResolveAsync("subject", org, default);
        Assert.Equal(expected, result.Outcome); Assert.Equal(reason, result.Reason);
        var scope = new TenantContextScope();
        var op = new TenantOperation(adapter, new Identity("subject", org), scope, scope);
        var context = new DefaultHttpContext();
        await new TenantAccessMiddleware(async _ => await op.AuthorizeAsync(new("ReferenceData.View"), default)).InvokeAsync(context);
        Assert.Equal(downstream, context.Response.StatusCode);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Theory]
    [InlineData("malformed", TenantAccessDiagnostics.ResponseMalformed)]
    [InlineData("integrity", TenantAccessDiagnostics.IntegrityMismatch)]
    public async Task Named_unsafe_authority_outcomes_are_non_disclosing_503(string failure, string reason)
    {
        var org = Guid.NewGuid();
        using var http = Http(new Handler(_ => Allowed(org, failure == "malformed" ? "version" : "subject")));
        var scope = new TenantContextScope();
        var op = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(reason, error.Reason);
        var context = new DefaultHttpContext();
        await new TenantAccessMiddleware(_ => throw error).InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static SignalCapture Listen(List<ActivityEvent> events) => new(events);
    private sealed class SignalCapture : IDisposable
    {
        private readonly Activity scope = new Activity("admin-authority-conformance").Start();
        private readonly ActivityListener listener;
        public SignalCapture(List<ActivityEvent> events)
        {
            var traceId = scope.TraceId;
            listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TenantAccessDiagnostics.DiagnosticsName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    if (activity.TraceId == traceId)
                        lock (events) events.AddRange(activity.Events);
                }
            };
            ActivitySource.AddActivityListener(listener);
        }
        public void Dispose() { listener.Dispose(); scope.Dispose(); }
    }
    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { BaseAddress = new("https://auth.invalid/") };
    private sealed record Credential(string? BearerToken = "synthetic-test-token") : ITenantAccessCredential;
    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private static HttpResponseMessage Allowed(Guid org, string invalid = "", string[]? permissions = null)
    {
        if (invalid == "json") return new(HttpStatusCode.OK) { Content = new StringContent("{private-invalid-json") };
        if (invalid == "null") return new(HttpStatusCode.OK) { Content = new StringContent("null") };
        if (invalid == "root_array") return new(HttpStatusCode.OK) { Content = new StringContent("[]") };
        string[]? codes = invalid switch
        {
            "permissions_null" => null,
            "duplicate" => ["ReferenceData.View", "ReferenceData.View"],
            "permission_empty" => [""],
            "permission_whitespace" => ["ReferenceData. View"],
            "permission_control" => ["ReferenceData.\nView"],
            "permission_segment" => ["ReferenceData..View"],
            _ => permissions ?? ["ReferenceData.View"]
        };
        var body = JsonSerializer.Serialize(new
        {
            ContractVersion = invalid == "version" ? 2 : 1,
            SubjectId = invalid == "subject" ? "different-private-subject" : "subject",
            OrganisationId = invalid == "organisation" ? Guid.NewGuid() : org,
            OrganisationMembershipId = invalid == "membership" ? Guid.Empty : Guid.NewGuid(),
            EffectivePermissionCodes = codes,
            DecisionVersion = invalid == "decision" ? "" : "v1",
            ObservedAtUtc = invalid == "observation" ? default : invalid == "offset"
                ? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)) : DateTimeOffset.UtcNow
        });
        if (invalid == "quoted_version") body = body.Replace("\"ContractVersion\":1", "\"ContractVersion\":\"1\"", StringComparison.Ordinal);
        if (invalid == "duplicate_json") body = body.Insert(1, "\"SubjectId\":\"subject\",");
        if (invalid == "duplicate_json_case") body = body.Insert(1, "\"subjectId\":\"subject\",");
        if (invalid == "missing_membership")
        {
            using var document = JsonDocument.Parse(body);
            body = JsonSerializer.Serialize(document.RootElement.EnumerateObject()
                .Where(p => p.Name != "OrganisationMembershipId").ToDictionary(p => p.Name, p => p.Value));
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
    }
}
