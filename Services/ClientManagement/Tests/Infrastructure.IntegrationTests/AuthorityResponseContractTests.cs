using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ClientManagement.API.Services;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "ZEK-17")]
public sealed class AuthorityResponseContractTests
{
    [Theory]
    [InlineData("subject", "subject_mismatch")]
    [InlineData("organisation", "organisation_mismatch")]
    [InlineData("membership", "contradictory_response")]
    public async Task Integrity_mismatch_is_503_and_emits_only_safe_operational_event_fields(string invalid, string mismatch)
    {
        var organisation = Guid.NewGuid();
        using var testScope = new Activity("test.authority.invocation").Start();
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(activity => { if (activity.TraceId == testScope.TraceId) activities.Add(activity); });
        var handler = new Handler(_ => Response(organisation, invalid));
        using var http = Client(handler);
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        var decision = await adapter.ResolveAsync("private-subject", organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, decision.Reason);
        Assert.Null(decision.Membership);
        var emitted = Assert.Single(activities.SelectMany(a => a.Events)
            .Where(e => e.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal(TimeSpan.Zero, emitted.Timestamp.Offset);
        Assert.All(emitted.Tags, tag => Assert.Contains(tag.Key,
            new[] { "event_code", "service", "mismatch_class", "contract_version" }));
        Assert.Equal(4, emitted.Tags.Count());
        Assert.Contains(emitted.Tags, tag => tag.Key == "event_code" && Equals(tag.Value, TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Contains(emitted.Tags, tag => tag.Key == "service" && Equals(tag.Value, "ClientManagement"));
        Assert.Contains(emitted.Tags, tag => tag.Key == "mismatch_class" && Equals(tag.Value, mismatch));
        Assert.Contains(emitted.Tags, tag => tag.Key == "contract_version" && Equals(tag.Value, 1));
        var allTags = activities.SelectMany(a => a.TagObjects).Concat(activities.SelectMany(a => a.Events).SelectMany(e => e.Tags));
        Assert.DoesNotContain(allTags, tag => (tag.Value?.ToString() ?? "").Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTags, tag => (tag.Value?.ToString() ?? "").Contains(organisation.ToString(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(allTags, tag => (tag.Value?.ToString() ?? "").Contains("Clients.View", StringComparison.Ordinal));
        Assert.Equal(1, handler.Calls);
        var scope = new TenantContextScope();
        var context = new DefaultHttpContext();
        var operation = new TenantOperation(new FixedAccess(decision), new Identity(organisation), scope, scope);
        await new TenantAccessMiddleware(async _ => await operation.AuthorizeAsync(new("ReferenceData.View"), default)).InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.ThrowsAny<InvalidOperationException>(() => scope.Current);
    }

    [Theory]
    [InlineData("start")] [InlineData("diagnostic-stop")] [InlineData("resolve-stop")]
    public async Task Throwing_operational_listener_cannot_authorize_integrity_failure(string stage)
    {
        using var testScope = new Activity("test.exporter.invocation").Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CurrentTenantAccessClient.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.TraceId == testScope.TraceId && stage == "start" && activity.OperationName == "tenant_access.diagnostic") throw new InvalidOperationException("private-exporter-error");
            },
            ActivityStopped = activity =>
            {
                if (activity.TraceId == testScope.TraceId && (stage == "diagnostic-stop" && activity.OperationName == "tenant_access.diagnostic"
                    || stage == "resolve-stop" && activity.OperationName == "tenant_access.resolve"))
                    throw new InvalidOperationException("private-exporter-error");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var organisation = Guid.NewGuid();
        using var http = Client(new Handler(_ => Response(organisation, "subject")));
        var result = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, result.Reason);
        Assert.Null(result.Membership);
    }

    [Theory]
    [InlineData("version")] [InlineData("duplicate")] [InlineData("permission")]
    [InlineData("decision")] [InlineData("observation")] [InlineData("null-permissions")]
    public async Task Malformed_response_is_distinct_from_dependency_failure(string invalid)
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler(_ => Response(organisation, invalid));
        using var http = Client(handler);
        var result = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(TenantAccessDiagnostics.ResponseMalformed, result.Reason);
        Assert.Equal(1, handler.Calls);
        Assert.Null(result.Membership);
    }

    [Theory]
    [InlineData(503)] [InlineData(500)] [InlineData(429)]
    public async Task Dependency_failure_is_named_and_bounded_to_two_safe_reads(int status)
    {
        var handler = new Handler(_ => new((HttpStatusCode)status));
        using var http = Client(handler);
        var result = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", Guid.NewGuid(), default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, result.Reason);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("Clients.ViewAll", "Clients.ViewAssigned")]
    [InlineData("ReferenceData.View", null)]
    public async Task Unknown_and_case_changed_extras_cannot_supply_a_missing_checked_grant(string permission, string? alternative)
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler(_ => Response(organisation, permissions: new[] { "Future.Permission", "clients.ViewAll", "Organisations.Close" }));
        using var http = Client(handler);
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity(organisation), scope, scope);
        var context = new DefaultHttpContext();
        await new TenantAccessMiddleware(async _ => await operation.AuthorizeAsync(new(permission, alternative), default)).InvokeAsync(context);
        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.ThrowsAny<InvalidOperationException>(() => scope.Current);
    }

    [Fact]
    public async Task Unknown_checked_code_is_named_configuration_denial_without_authority_or_context()
    {
        using var testScope = new Activity("test.authority.invocation").Start();
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(activity => { if (activity.TraceId == testScope.TraceId) activities.Add(activity); });
        var scope = new TenantContextScope();
        var access = new CountingAccess(new(TenantAccessOutcome.Authorized,
            new(Guid.NewGuid(), Guid.NewGuid(), ["Unknown.Checked"], "1", DateTimeOffset.UtcNow)));
        var operation = new TenantOperation(access, new Identity(Guid.NewGuid()), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("Unknown.Checked"), default));
        Assert.Equal(AccessFailure.Denied, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.PermissionCheckUnknown, error.Reason);
        Assert.Equal(0, access.Calls);
        Assert.Contains(activities.SelectMany(a => a.Events), e => e.Name == TenantAccessDiagnostics.PermissionCheckUnknown);
        Assert.ThrowsAny<InvalidOperationException>(() => scope.Current);
    }

    [Theory]
    [InlineData(403)] [InlineData(503)]
    public async Task Established_scope_rechecks_current_authority_and_recovers_without_caching(int failedStatus)
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler(call => call == 1 || call >= (failedStatus == 503 ? 4 : 3)
            ? Response(organisation) : new((HttpStatusCode)failedStatus));
        using var http = Client(handler);
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        var failure = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(failedStatus == 403 ? AccessFailure.Denied : AccessFailure.Unavailable, failure.Failure);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        Assert.Equal(failedStatus == 503 ? 4 : 3, handler.Calls);
    }

    [Fact]
    public async Task Denied_operation_obtains_a_fresh_later_authorized_decision()
    {
        var organisation = Guid.NewGuid();
        var handler = new Handler(call => call == 1 ? new(HttpStatusCode.Forbidden) : Response(organisation));
        using var http = Client(handler);
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity(organisation), scope, scope);
        Assert.Equal(AccessFailure.Denied, (await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default))).Failure);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        Assert.Equal(2, handler.Calls);
    }

    [Theory]
    [InlineData("duplicate-subject")] [InlineData("case-duplicate-subject")]
    [InlineData("missing-membership")] [InlineData("quoted-version")] [InlineData("non-utc")]
    public async Task Ambiguous_or_incomplete_wire_schema_is_malformed(string invalid)
    {
        var organisation = Guid.NewGuid();
        using var original = Response(organisation);
        var json = await original.Content.ReadAsStringAsync();
        json = invalid switch
        {
            "duplicate-subject" => json[..^1] + ",\"subjectId\":\"private-other-subject\"}",
            "case-duplicate-subject" => json[..^1] + ",\"SUBJECTID\":\"private-other-subject\"}",
            "missing-membership" => json.Replace("organisationMembershipId", "omittedMembership", StringComparison.Ordinal),
            "quoted-version" => json.Replace("\"contractVersion\":1", "\"contractVersion\":\"1\"", StringComparison.Ordinal),
            "non-utc" => json.Replace("+00:00", "+01:00", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) });
        using var http = Client(handler);
        var result = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", organisation, default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(TenantAccessDiagnostics.ResponseMalformed, result.Reason);
        Assert.Equal(1, handler.Calls);
    }
    private static ActivityListener Listen(Action<Activity> completed)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CurrentTenantAccessClient.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = completed
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("https://auth.invalid/") };
    private sealed record Credential(string? BearerToken) : ITenantAccessCredential
    {
        public Credential() : this(Guid.NewGuid().ToString("N")) { }
    }
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "private-subject"; }
    private sealed record FixedAccess(TenantAccessDecision Decision) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId, CancellationToken cancellationToken) => Task.FromResult(Decision);
    }
    private sealed class CountingAccess(TenantAccessDecision decision) : ICurrentTenantAccess
    {
        public int Calls { get; private set; }
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId, CancellationToken cancellationToken) { Calls++; return Task.FromResult(decision); }
    }
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(++Calls));
    }
    private static readonly Guid Membership = Guid.NewGuid();
    private static HttpResponseMessage Response(Guid organisation, string invalid = "", string[]? permissions = null) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            ContractVersion = invalid == "version" ? 2 : 1,
            SubjectId = invalid == "subject" ? "private-other-subject" : "private-subject",
            OrganisationId = invalid == "organisation" ? Guid.NewGuid() : organisation,
            OrganisationMembershipId = invalid == "membership" ? Guid.Empty : Membership,
            EffectivePermissionCodes = invalid == "null-permissions" ? null : invalid == "duplicate" ? new[] { "ReferenceData.View", "ReferenceData.View" }
                : invalid == "permission" ? new[] { "invalid permission" } : permissions ?? new[] { "ReferenceData.View" },
            DecisionVersion = invalid == "decision" ? "" : "1",
            ObservedAtUtc = invalid == "observation" ? default : DateTimeOffset.UtcNow
        })
    };
}