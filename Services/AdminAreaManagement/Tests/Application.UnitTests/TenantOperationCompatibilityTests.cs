using System.Diagnostics;
using AdminAreaManagement.Application.Common.Authorization;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Application.UnitTests;

[Trait("Issue", "17")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class TenantOperationCompatibilityTests
{
    [Fact]
    public async Task Unknown_extras_do_not_reject_valid_known_grants_or_grant_checked_authority()
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            Grant(org, ["ReferenceData.View", "Organisations.Export", "Organisations.Close", "Unknown"])));
        var op = new TenantOperation(access, new Identity("subject", org), scope, scope);
        await op.AuthorizeAsync(new("ReferenceData.View"), default);
        Assert.False(op.AssignedOnly);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("Unknown"), default));
        Assert.Equal(AccessFailure.Denied, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.PermissionCheckUnknown, error.Reason);
        Assert.Equal(1, access.Calls);
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Denied, AccessFailure.Denied)]
    [InlineData(TenantAccessOutcome.Unavailable, AccessFailure.Unavailable)]
    public async Task Every_protected_operation_observes_revocation_or_unavailability_without_cached_authority(TenantAccessOutcome later, AccessFailure expected)
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized, Grant(org, ["ReferenceData.View"])));
        var op = new TenantOperation(access, new Identity("subject", org), scope, scope);
        await op.AuthorizeAsync(new("ReferenceData.View"), default);
        access.Decision = new(later);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(expected, error.Failure); Assert.Equal(2, access.Calls);
    }

    [Fact]
    public async Task Earlier_denial_is_not_cached_and_cannot_replace_a_fresh_positive_decision()
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Denied));
        var op = new TenantOperation(access, new Identity("subject", org), scope, scope);
        await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Throws<TenantContextException>(() => scope.Current);
        access.Decision = new(TenantAccessOutcome.Authorized, Grant(org, ["ReferenceData.View"]));
        await op.AuthorizeAsync(new("ReferenceData.View"), default);
        Assert.Equal(org, scope.Current.OrganisationId.Value); Assert.Equal(2, access.Calls);
    }

    [Theory]
    [InlineData("Clients.ViewAll", null)]
    [InlineData("Clients.ViewAll", "Clients.ViewAssigned")]
    public async Task Absent_checked_grants_are_denied_without_alternative_invention(string required, string? alternative)
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized, Grant(org, ["ReferenceData.View", "Organisations.Close"])));
        var op = new TenantOperation(access, new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new(required, alternative), default));
        Assert.Equal(AccessFailure.Denied, error.Failure);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Fact]
    public async Task Unknown_checked_code_emits_named_safe_configuration_signal_and_does_not_contact_authority()
    {
        var events = new List<ActivityEvent>(); using var listener = Listen(events);
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized, Grant(org, ["ReferenceData.View"])));
        var op = new TenantOperation(access, new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("Unknown.Check"), default));
        Assert.Equal(AccessFailure.Denied, error.Failure); Assert.Equal(TenantAccessDiagnostics.PermissionCheckUnknown, error.Reason);
        Assert.Equal(0, access.Calls);
        var signal = Assert.Single(events.Where(e => e.Name == TenantAccessDiagnostics.PermissionCheckUnknown));
        Assert.Equal(new[] { "contract_version", "event_code", "service" }, signal.Tags.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(signal.Tags, t => t.Value?.ToString() == "Unknown.Check");
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Theory]
    [InlineData("null_success")] [InlineData("denied_grant")] [InlineData("organisation")]
    [InlineData("membership")] [InlineData("unauthenticated_grant")] [InlineData("unavailable_grant")]
    public async Task Contradictory_success_or_decision_fails_503_with_operational_integrity_signal(string contradiction)
    {
        var events = new List<ActivityEvent>(); using var listener = Listen(events);
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var grant = Grant(contradiction == "organisation" ? Guid.NewGuid() : org, ["ReferenceData.View"]);
        if (contradiction == "membership") grant = grant with { OrganisationMembershipId = Guid.Empty };
        var outcome = contradiction switch
        {
            "denied_grant" => TenantAccessOutcome.Denied,
            "unauthenticated_grant" => TenantAccessOutcome.Unauthenticated,
            "unavailable_grant" => TenantAccessOutcome.Unavailable,
            _ => TenantAccessOutcome.Authorized
        };
        var decision = contradiction == "null_success" ? new TenantAccessDecision(TenantAccessOutcome.Authorized)
            : new(outcome, grant);
        var op = new TenantOperation(new Access(decision), new Identity("private-subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure); Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, error.Reason);
        var signal = Assert.Single(events.Where(e => e.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.All(signal.Tags, tag => Assert.Contains(tag.Key, new[] { "event_code", "service", "mismatch_class", "contract_version" }));
        Assert.DoesNotContain(signal.Tags, t => (t.Value?.ToString() ?? "").Contains("private", StringComparison.Ordinal));
        Assert.DoesNotContain(signal.Tags, t => t.Value?.ToString() == org.ToString());
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Theory]
    [InlineData("permissions_null")] [InlineData("duplicate")] [InlineData("permission_invalid")]
    [InlineData("decision")] [InlineData("observation")] [InlineData("offset")]
    public async Task Invalid_grant_shape_is_malformed_not_an_ordinary_denial(string invalid)
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var grant = Grant(org, ["ReferenceData.View"]);
        grant = invalid switch
        {
            "permissions_null" => grant with { EffectivePermissionCodes = null! },
            "duplicate" => grant with { EffectivePermissionCodes = ["ReferenceData.View", "ReferenceData.View"] },
            "permission_invalid" => grant with { EffectivePermissionCodes = ["invalid permission"] },
            "decision" => grant with { DecisionVersion = "" },
            "offset" => grant with { ObservedAtUtc = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)) },
            _ => grant with { ObservedAtUtc = default }
        };
        var op = new TenantOperation(new Access(new(TenantAccessOutcome.Authorized, grant)), new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure); Assert.Equal(TenantAccessDiagnostics.ResponseMalformed, error.Reason);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Unauthenticated, AccessFailure.Unauthenticated, null)]
    [InlineData(TenantAccessOutcome.Denied, AccessFailure.Denied, null)]
    [InlineData(TenantAccessOutcome.Unavailable, AccessFailure.Unavailable, TenantAccessDiagnostics.DependencyUnavailable)]
    public async Task Unknown_reason_text_is_not_copied_into_authorization_exceptions(TenantAccessOutcome outcome, AccessFailure expected, string? expectedReason)
    {
        var privateReason = Guid.NewGuid().ToString("N");
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var op = new TenantOperation(new Access(new(outcome, Reason: privateReason)), new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(expected, error.Failure); Assert.Equal(expectedReason, error.Reason);
        Assert.DoesNotContain(privateReason, error.ToString(), StringComparison.Ordinal);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Fact]
    public async Task Returned_grant_comparison_is_ordinal_and_cannot_promote_different_case()
    {
        var org = Guid.NewGuid(); var scope = new TenantContextScope();
        var op = new TenantOperation(new Access(new(TenantAccessOutcome.Authorized, Grant(org, ["referencedata.View"]))),
            new Identity("subject", org), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => op.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Denied, error.Failure);
    }

    private static AuthorizedTenantMembership Grant(Guid org, IReadOnlyCollection<string> permissions) =>
        new(org, Guid.NewGuid(), permissions, "v1", DateTimeOffset.UtcNow);
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
    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class Access(TenantAccessDecision decision) : ICurrentTenantAccess
    {
        public int Calls { get; private set; }
        public TenantAccessDecision Decision { get; set; } = decision;
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Decision); }
    }
}
