using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Supports.Queries.GetSupportsByReferents;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Application.UnitTests;

public sealed class MySupportsAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Handler_passes_the_live_membership_for_assigned_and_all_scope(bool all)
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid(); var scope = new TenantContextScope();
        var operation = new TenantOperation(new Access(organisation, membership, all), new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("Clients.ViewAll", "Clients.ViewAssigned"), default);
        // The port probe stops before sorting/pagination; it verifies the security identity at the Application boundary.
        var support = System.Reflection.DispatchProxy.Create<ISupportRepository, SupportProbe>();
        ((SupportProbe)(object)support).ExpectedMembership = membership;
        var repository = System.Reflection.DispatchProxy.Create<IRepositoryManager, RepositoryProbe>();
        ((RepositoryProbe)(object)repository).Support = support;
        var handler = new GetSupportsBySocialWorkersQuery.GetClientsByStaffMembersQueryHandler(repository, operation, null!, null!);
        await Assert.ThrowsAsync<ReachedPort>(() => handler.Handle(new() { IsActive = true }, default));
    }

    [Fact]
    public async Task Handler_rejects_missing_authorized_linkage_before_touching_persistence()
    {
        var scope = new TenantContextScope();
        var operation = new TenantOperation(null!, new Identity(Guid.NewGuid()), scope, scope);
        var handler = new GetSupportsBySocialWorkersQuery.GetClientsByStaffMembersQueryHandler(null!, operation, null!, null!);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => handler.Handle(new(), default));
        Assert.Equal(AccessFailure.Denied, error.Failure);
    }

    public class SupportProbe : System.Reflection.DispatchProxy
    {
        public Guid ExpectedMembership { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            Assert.Equal(nameof(ISupportRepository.GetConsultantSupportsByMembership), method!.Name);
            Assert.Equal(ExpectedMembership, args![0]);
            throw new ReachedPort();
        }
    }
    public class RepositoryProbe : System.Reflection.DispatchProxy
    {
        public ISupportRepository Support { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        { Assert.Equal("get_Support", method!.Name); return Support; }
    }
    public sealed class ReachedPort : Exception { }
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "subject"; }
    private sealed record Access(Guid Organisation, Guid Membership, bool All) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken token) =>
            Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
                new(Organisation, Membership, new[] { All ? "Clients.ViewAll" : "Clients.ViewAssigned" }, "1", DateTimeOffset.UtcNow)));
    }
}
