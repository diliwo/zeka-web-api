using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;

namespace Domain.UnitTests;

public sealed class LifecycleRetentionStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Successor_registry_freezes_four_exact_disposition_category_owners()
    {
        var predecessor = ReviewedClosureRegistryV1.Create();
        var successor = ReviewedDispositionRegistryV1.Create();
        Assert.Equal(11, predecessor.Inventory.Count);
        Assert.Equal(15, successor.Inventory.Count);
        Assert.True(ReviewedDispositionRegistryV1.HasCompleteInventory(successor));
        Assert.Equal(4, successor.Inventory.Count(x => x.Capability.Key ==
            ReviewedDispositionRegistryV1.CapabilityKey));
        Assert.All(successor.Inventory.Where(x => x.Capability.Key ==
            ReviewedDispositionRegistryV1.CapabilityKey), x =>
        {
            Assert.True(x.Mandatory);
            Assert.Equal(1, x.ContractVersion);
        });
        Assert.NotEqual(predecessor.InventoryHash, successor.InventoryHash);
    }

    [Fact]
    public void Predecessor_operation_does_not_acquire_successor_disposition_inventory()
    {
        var old = LifecycleOperation.Admit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), ReviewedClosureRegistryV1.Create(), Now);
        Assert.Null(old.DispositionInventoryHash);
        Assert.False(ReviewedDispositionRegistryV1.HasCompleteFrozenInventory(old.Participants));
        Assert.Throws<InvalidOperationException>(() => old.MarkDispositionReady(
            Now.AddSeconds(2), new string('a', 64)));
    }

    [Fact]
    public void Archive_completes_only_closure_phase_and_same_operation_advances_to_readiness()
    {
        var registry = ReviewedDispositionRegistryV1.Create();
        var organisation = Organisation.Create(Guid.NewGuid(), "Example", Guid.NewGuid(), Now);
        Assert.True(organisation.Activate(Now));
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), organisation.Id, Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), registry, Now);
        var identity = operation.Id;
        var frozenInventory = operation.InventoryHash;
        var categoryHash = operation.DispositionInventoryHash;
        Assert.NotNull(categoryHash);
        Assert.True(ReviewedDispositionRegistryV1.HasCompleteFrozenInventory(operation.Participants));
        Assert.True(organisation.BeginClosure(Now.AddSeconds(1)));
        operation.BeginTermination(Now.AddSeconds(1));
        foreach (var owner in registry.Inventory.Where(x => x.Capability.Key ==
                     ReviewedClosureRegistryV1.CapabilityKey))
            operation.RecordClosureAccepted(owner.ParticipantId);
        Assert.True(organisation.Archive(Now.AddSeconds(2)));
        operation.CompleteClosureArchive(Now.AddSeconds(2), new string('a', 64));
        Assert.Equal(LifecycleOperationState.Archived, operation.State);
        Assert.True(operation.IsActive);
        Assert.Null(operation.CompletedAt);
        Assert.Equal(Now.AddSeconds(2), operation.ArchivedAt);
        Assert.Equal(new string('a', 64), operation.ClosureFenceEvidenceHash);

        Assert.True(organisation.MarkDispositionReady(Now.AddSeconds(3)));
        operation.MarkDispositionReady(Now.AddSeconds(3), new string('b', 64));
        Assert.Equal(identity, operation.Id);
        Assert.Equal(frozenInventory, operation.InventoryHash);
        Assert.Equal(categoryHash, operation.DispositionInventoryHash);
        Assert.Equal(LifecycleOperationState.DispositionReady, operation.State);
        Assert.Equal(OrganisationStatus.DispositionReady, organisation.Status);
        Assert.True(operation.IsActive);
        Assert.Null(operation.CompletedAt);
        Assert.Equal(new string('a', 64), operation.ClosureFenceEvidenceHash);
        Assert.Equal(new string('b', 64), operation.RetentionDecisionSetHash);
    }
}
