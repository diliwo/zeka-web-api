using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;

namespace Domain.UnitTests;

public sealed class LifecycleClosureStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reviewed_registry_has_the_exact_frozen_LIFE02_inventory()
    {
        var registry = ReviewedClosureRegistryV1.Create();

        Assert.Equal(ReviewedClosureRegistryV1.Revision, registry.Revision);
        Assert.Equal(ReviewedClosureRegistryV1.ReviewReference, registry.ReviewReference);
        var closure = registry.Inventory.Where(x =>
            x.Capability.Family == LifecycleOperationFamily.Termination).ToArray();
        Assert.Equal(11, registry.Inventory.Count);
        Assert.Equal(4, closure.Length);
        Assert.Equal(
            ["admin-area", "admin-area-documents", "auth-management", "client-management"],
            closure.Select(x => x.ParticipantId).Order(StringComparer.Ordinal));
        Assert.All(closure, binding =>
        {
            Assert.Equal(LifecycleOperationFamily.Termination, binding.Capability.Family);
            Assert.Equal(ReviewedClosureRegistryV1.CapabilityKey, binding.Capability.Key);
            Assert.True(binding.Mandatory);
            Assert.Equal(1, binding.ContractVersion);
        });
    }

    [Fact]
    public void Termination_archives_only_after_every_frozen_participant_accepts()
    {
        var registry = ReviewedClosureRegistryV1.Create();
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), registry, Now);

        operation.BeginTermination(Now.AddSeconds(1));
        var closure = registry.Inventory.Where(x =>
            x.Capability.Family == LifecycleOperationFamily.Termination).ToArray();
        foreach (var participant in closure.Take(3))
            operation.RecordClosureAccepted(participant.ParticipantId);
        Assert.Throws<InvalidOperationException>(() => operation.CompleteTermination(
            Now.AddSeconds(2), new string('a', 64)));

        operation.RecordClosureAccepted(closure[3].ParticipantId);
        operation.CompleteTermination(Now.AddSeconds(2), new string('a', 64));
        Assert.Equal(LifecycleOperationState.Completed, operation.State);
        Assert.False(operation.IsActive);
    }

    [Fact]
    public void Pre_archive_recovery_releases_only_established_fences_and_is_terminal()
    {
        var registry = ReviewedClosureRegistryV1.Create();
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), registry, Now);
        operation.BeginTermination(Now.AddSeconds(1));
        operation.RecordClosureAccepted("auth-management");
        Assert.Throws<InvalidOperationException>(operation.BeginClosureRecovery);
        foreach (var participant in new[] { "admin-area", "admin-area-documents", "client-management" })
            operation.RecordClosureFailure(participant, "participant-unavailable", true,
                Now.AddSeconds(2), LifecycleClosureBoundaryDisposition.NotEstablished);

        operation.BeginClosureRecovery();
        Assert.False(operation.CanCompleteClosureRecovery());
        operation.RecordClosureReleased("auth-management");
        Assert.True(operation.CanCompleteClosureRecovery());
        operation.CompleteClosureRecovery(Now.AddSeconds(3));

        Assert.Equal(LifecycleOperationState.Failed, operation.State);
        Assert.Equal("ClosureRecoveredBeforeArchive", operation.FailureCode);
        Assert.False(operation.IsActive);
    }

    [Fact]
    public void Organisation_preserves_legacy_closed_and_adds_separate_closing_archive_transitions()
    {
        var organisation = Organisation.Create(Guid.NewGuid(), "Example", Guid.NewGuid(), Now);
        Assert.True(organisation.Activate(Now));
        Assert.True(organisation.BeginClosure(Now.AddSeconds(1)));
        Assert.Equal(OrganisationStatus.Closing, organisation.Status);
        Assert.True(organisation.Archive(Now.AddSeconds(2)));
        Assert.Equal(OrganisationStatus.Archived, organisation.Status);
        Assert.False(organisation.RecoverClosure(Now.AddSeconds(3)));
        Assert.Equal(4, (int)OrganisationStatus.Closed);
    }
}
