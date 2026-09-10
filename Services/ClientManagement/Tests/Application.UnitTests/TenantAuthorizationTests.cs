using ClientManagement.Application.Common.Authorization;
using MediatR;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Application.UnitTests;

public sealed class TenantAuthorizationTests
{
    [Fact]
    public void Every_current_application_request_has_an_explicit_permission()
    {
        var assembly = typeof(TenantOperation).Assembly;
        var requests = assembly.GetTypes().Where(type => !type.IsAbstract && typeof(IBaseRequest).IsAssignableFrom(type)).ToArray();
        Assert.NotEmpty(requests);
        foreach (var request in requests)
        {
            var policy = Assert.Single(request.GetCustomAttributes(typeof(RequiresTenantPermissionAttribute), false))
                as RequiresTenantPermissionAttribute;
            Assert.NotNull(policy);
            Assert.True(PermissionCatalogue.IsKnown(policy!.Permission) || policy.Permission == "Platform.ReferenceData.Manage", request.FullName);
            if (policy.AssignedAlternative is not null) Assert.True(PermissionCatalogue.IsKnown(policy.AssignedAlternative));
        }
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Denied, AccessFailure.Denied)]
    [InlineData(TenantAccessOutcome.Unavailable, AccessFailure.Unavailable)]
    public async Task Failed_current_decisions_never_establish_tenant_context(TenantAccessOutcome outcome, AccessFailure expected)
    {
        var scope = new TenantContextScope();
        var identity = new Identity("subject", Guid.NewGuid());
        var access = new Access(new(outcome));
        var operation = new TenantOperation(access, identity, scope, scope);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(
            new("ReferenceData.View"), CancellationToken.None));
        Assert.Equal(expected, error.Failure);
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Fact]
    public async Task Selection_alone_unknown_permissions_and_forged_organisations_fail_closed()
    {
        var selected = Guid.NewGuid();
        foreach (var decision in new[]
        {
            new AuthorizedTenantMembership(selected, Guid.NewGuid(), [], "v1", DateTimeOffset.UtcNow),
            new AuthorizedTenantMembership(Guid.NewGuid(), Guid.NewGuid(), ["Clients.ViewAll"], "v1", DateTimeOffset.UtcNow),
            new AuthorizedTenantMembership(selected, Guid.Empty, ["Clients.ViewAll"], "v1", DateTimeOffset.UtcNow),
            new AuthorizedTenantMembership(selected, Guid.NewGuid(), ["Clients.ViewAll", "Unknown"], "v1", DateTimeOffset.UtcNow)
        })
        {
            var scope = new TenantContextScope();
            var operation = new TenantOperation(new Access(new(TenantAccessOutcome.Authorized, decision)),
                new Identity("subject", selected), scope, scope);
            await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(
                new("Clients.ViewAll", "Clients.ViewAssigned"), CancellationToken.None));
            Assert.Throws<TenantContextException>(() => scope.Current);
        }
    }

    [Fact]
    public async Task Assigned_permission_carries_membership_identity_without_broadening_access()
    {
        var selected = Guid.NewGuid(); var membership = Guid.NewGuid();
        var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(selected, membership, ["Clients.ViewAssigned"], "v1", DateTimeOffset.UtcNow)));
        var operation = new TenantOperation(access, new Identity("subject", selected), scope, scope);
        await operation.AuthorizeAsync(new("Clients.ViewAll", "Clients.ViewAssigned"), CancellationToken.None);
        Assert.True(operation.AssignedOnly);
        Assert.Equal(membership, operation.MembershipId);
        Assert.Equal(selected, scope.Current.OrganisationId.Value);
        access.Decision = new(TenantAccessOutcome.Denied);
        await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(
            new("Clients.ViewAll", "Clients.ViewAssigned"), CancellationToken.None));
        Assert.Equal(2, access.Calls); // No positive authorization cache.
    }

    [Fact]
    public async Task Tenant_grants_cannot_authorize_platform_reference_mutations()
    {
        var selected = Guid.NewGuid(); var scope = new TenantContextScope();
        var access = new Access(new(TenantAccessOutcome.Authorized,
            new(selected, Guid.NewGuid(), ["ReferenceData.View"], "v1", DateTimeOffset.UtcNow)));
        var operation = new TenantOperation(access, new Identity("subject", selected), scope, scope);
        await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(
            new("Platform.ReferenceData.Manage"), CancellationToken.None));
        Assert.Throws<TenantContextException>(() => scope.Current);
    }

    [Fact]
    public async Task Parallel_http_worker_and_consumer_operations_have_independent_contexts()
    {
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async index =>
        {
            var id = Guid.NewGuid(); var scope = new TenantContextScope();
            var access = new Access(new(TenantAccessOutcome.Authorized,
                new(id, Guid.NewGuid(), ["ReferenceData.View"], "v1", DateTimeOffset.UtcNow)));
            var operation = new TenantOperation(access, new Identity(index.ToString(), id), scope, scope);
            await operation.AuthorizeAsync(new("ReferenceData.View"), CancellationToken.None);
            await Task.Yield();
            Assert.Equal(id, scope.Current.OrganisationId.Value);
        }));
        Assert.Throws<TenantContextException>(() => new TenantContextScope().Current);
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
