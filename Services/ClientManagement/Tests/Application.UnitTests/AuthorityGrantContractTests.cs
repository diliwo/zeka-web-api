using System.Collections.Concurrent;
using System.Diagnostics;
using ClientManagement.Application.Common.Authorization;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Application.UnitTests;

[Trait("Issue", "ZEK-17")]
public sealed class AuthorityGrantContractTests
{
    [Fact]
    public async Task Operation_independently_ignores_extras_without_broadening_assigned_scope()
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(organisation, membership, ["Clients.ViewAssigned", "Organisations.Close", "clients.ViewAll"], "1", DateTimeOffset.UtcNow)));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("Clients.ViewAll", "Clients.ViewAssigned"), default);
        Assert.True(operation.AssignedOnly);
        Assert.False(operation.CanEditAll);
        Assert.False(operation.CanEditAssigned);
        Assert.Equal(membership, operation.MembershipId);
        var denied = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("Organisations.Close"), default));
        Assert.Equal(AccessFailure.Denied, denied.Failure);
        Assert.Equal(TenantAccessDiagnostics.PermissionCheckUnknown, denied.Reason);
        Assert.Equal(1, access.Calls);
    }

    [Theory]
    [InlineData("null")] [InlineData("organisation")]
    [InlineData("membership")] [InlineData("denied-with-membership")]
    [InlineData("unauthenticated-with-membership")] [InlineData("unavailable-with-membership")]
    public async Task Contradictory_success_or_decision_is_unavailable_with_observed_operational_event(string invalid)
    {
        var organisation = Guid.NewGuid();
        using var testScope = new Activity("test.authority.invocation").Start();
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "ClientManagement.TenantAccess",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { if (activity.TraceId == testScope.TraceId) activities.Add(activity); }
        };
        ActivitySource.AddActivityListener(listener);
        var grant = invalid == "null" ? null : new AuthorizedTenantMembership(
            invalid == "organisation" ? Guid.NewGuid() : organisation,
            invalid == "membership" ? Guid.Empty : Guid.NewGuid(), ["ReferenceData.View"], "1", DateTimeOffset.UtcNow);
        var outcome = invalid switch
        {
            "denied-with-membership" => TenantAccessOutcome.Denied,
            "unauthenticated-with-membership" => TenantAccessOutcome.Unauthenticated,
            "unavailable-with-membership" => TenantAccessOutcome.Unavailable,
            _ => TenantAccessOutcome.Authorized
        };
        var access = new Access(new(outcome, grant));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, error.Reason);
        var emitted = Assert.Single(activities.SelectMany(activity => activity.Events).Where(e => e.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.All(emitted.Tags, tag => Assert.Contains(tag.Key, new[] { "event_code", "service", "mismatch_class", "contract_version" }));
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("permission")]
    [InlineData("decision")] [InlineData("timestamp")] [InlineData("non-utc")]
    public async Task Operation_rejects_malformed_grants_without_establishing_context(string invalid)
    {
        var organisation = Guid.NewGuid();
        var permissions = invalid == "duplicate" ? new[] { "ReferenceData.View", "ReferenceData.View" }
            : new[] { invalid == "permission" ? "invalid permission" : "ReferenceData.View" };
        var grant = new AuthorizedTenantMembership(organisation, Guid.NewGuid(), permissions,
            invalid == "decision" ? "" : "1", invalid == "timestamp" ? default
                : invalid == "non-utc" ? DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(1)) : DateTimeOffset.UtcNow);
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new Access(new(TenantAccessOutcome.Authorized, grant)), new Identity(organisation), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.ResponseMalformed, error.Reason);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Fact]
    public async Task Every_authorization_reentry_obtains_a_new_decision_without_positive_or_negative_cache()
    {
        var organisation = Guid.NewGuid();
        var grant = new AuthorizedTenantMembership(organisation, Guid.NewGuid(), ["ReferenceData.View"], "1", DateTimeOffset.UtcNow);
        var access = new Access(new(TenantAccessOutcome.Denied));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        Assert.Equal(AccessFailure.Denied, (await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default))).Failure);
        access.Decision = new(TenantAccessOutcome.Authorized, grant);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        access.Decision = new(TenantAccessOutcome.Unavailable, Reason: TenantAccessDiagnostics.DependencyUnavailable);
        Assert.Equal(AccessFailure.Unavailable, (await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default))).Failure);
        access.Decision = new(TenantAccessOutcome.Authorized, grant);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        Assert.Equal(4, access.Calls);
    }

    [Fact]
    public async Task Established_context_cannot_adopt_a_different_membership()
    {
        var organisation = Guid.NewGuid();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(organisation, Guid.NewGuid(), ["ReferenceData.View"], "1", DateTimeOffset.UtcNow)));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        access.Decision = new(TenantAccessOutcome.Authorized,
            new(organisation, Guid.NewGuid(), ["ReferenceData.View"], "2", DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, error.Reason);
        Assert.Equal(2, access.Calls);
    }

    [Theory]
    [InlineData("Clients.EditAll")] [InlineData("Clients.EditAssigned")]
    public async Task Successful_reentry_does_not_retain_a_revoked_edit_capability(string editPermission)
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(organisation, membership, ["Clients.ViewAll", editPermission], "1", DateTimeOffset.UtcNow)));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        await operation.AuthorizeBeneficiaryReadAsync(default);
        Assert.True(editPermission == "Clients.EditAll" ? operation.CanEditAll : operation.CanEditAssigned);
        access.Decision = new(TenantAccessOutcome.Authorized,
            new(organisation, membership, ["Clients.ViewAll"], "2", DateTimeOffset.UtcNow));
        await operation.AuthorizeBeneficiaryReadAsync(default);
        Assert.False(operation.CanEditAll);
        Assert.False(operation.CanEditAssigned);
        Assert.Equal(2, access.Calls);
    }
    [Theory]
    [InlineData(TenantAccessOutcome.Unauthenticated, AccessFailure.Unauthenticated)]
    [InlineData(TenantAccessOutcome.Denied, AccessFailure.Denied)]
    [InlineData(TenantAccessOutcome.Unavailable, AccessFailure.Unavailable)]
    public async Task Untrusted_free_text_decision_reasons_are_not_exported(TenantAccessOutcome outcome, AccessFailure expected)
    {
        var scope = new TenantContextScope();
        var access = new Access(new(outcome, Reason: "private-untrusted-diagnostic-value"));
        var operation = new TenantOperation(access, new Identity(Guid.NewGuid()), scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(outcome == TenantAccessOutcome.Unavailable ? TenantAccessDiagnostics.DependencyUnavailable : null, error.Reason);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }
    [Theory]
    [InlineData(TenantAccessOutcome.Denied)]
    [InlineData(TenantAccessOutcome.Unavailable)]
    public async Task Refused_reentry_exposes_no_prior_edit_capabilities(TenantAccessOutcome outcome)
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(organisation, membership, ["Clients.ViewAll", "Clients.EditAll", "Clients.EditAssigned"], "1", DateTimeOffset.UtcNow)));
        var scope = new TenantContextScope();
        var operation = new TenantOperation(access, new Identity(organisation), scope, scope);
        await operation.AuthorizeBeneficiaryReadAsync(default);
        Assert.True(operation.CanEditAll);
        Assert.True(operation.CanEditAssigned);
        access.Decision = new(outcome);
        var refusal = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeBeneficiaryReadAsync(default));
        Assert.Equal(outcome == TenantAccessOutcome.Denied ? AccessFailure.Denied : AccessFailure.Unavailable, refusal.Failure);
        Assert.False(operation.CanEditAll);
        Assert.False(operation.CanEditAssigned);
        Assert.Equal(2, access.Calls);
    }

    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "private-subject"; }
    private sealed class Access(TenantAccessDecision decision) : ICurrentTenantAccess
    {
        public int Calls { get; private set; }
        public TenantAccessDecision Decision { get; set; } = decision;
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Decision); }
    }
}