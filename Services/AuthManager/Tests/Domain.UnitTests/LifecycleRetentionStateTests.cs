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

    [Fact]
    public void Purge_boundary_preserves_same_operation_and_never_claims_verified_completion()
    {
        var registry = ReviewedDispositionRegistryV1.Create();
        var organisation = Organisation.Create(Guid.NewGuid(), "Fixture", Guid.NewGuid(), Now);
        Assert.True(organisation.Activate(Now));
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), organisation.Id, Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), registry, Now);
        Assert.True(organisation.BeginClosure(Now.AddSeconds(1)));
        operation.BeginTermination(Now.AddSeconds(1));
        foreach (var owner in registry.Inventory.Where(x => x.Capability.Key ==
                     ReviewedClosureRegistryV1.CapabilityKey))
            operation.RecordClosureAccepted(owner.ParticipantId);
        Assert.True(organisation.Archive(Now.AddSeconds(2)));
        operation.CompleteClosureArchive(Now.AddSeconds(2), new string('a', 64));
        Assert.True(organisation.MarkDispositionReady(Now.AddSeconds(3)));
        operation.MarkDispositionReady(Now.AddSeconds(3), new string('b', 64));

        Assert.Throws<InvalidOperationException>(() => operation.BeginPurge(Now.AddSeconds(4),
            new string('c', 64), new string('d', 64), new string('e', 64)));
        Assert.True(organisation.BeginPurge(Now.AddSeconds(4)));
        operation.BeginPurge(Now.AddSeconds(4), new string('b', 64),
            new string('d', 64), new string('e', 64));
        Assert.Equal(LifecycleOperationState.PurgeInProgress, operation.State);
        Assert.Equal(OrganisationStatus.PurgeInProgress, organisation.Status);
        Assert.Equal(operation.Revision, operation.IrreversibleRevision);
        Assert.Equal(Now.AddSeconds(2), operation.ArchivedAt);
        Assert.Null(operation.CompletedAt);
        Assert.True(operation.IsActive);
        Assert.Throws<InvalidOperationException>(() => operation.BeginPurge(Now.AddSeconds(5),
            new string('b', 64), new string('d', 64), new string('e', 64)));
        Assert.Throws<InvalidOperationException>(() => operation.Fail("ParticipantUnavailable"));

        operation.MarkPurgeExecutionComplete(Now.AddSeconds(5));
        Assert.Equal(LifecycleOperationState.PurgeExecutionComplete, operation.State);
        Assert.Equal(OrganisationStatus.PurgeInProgress, organisation.Status);
        Assert.Null(operation.CompletedAt);
        Assert.True(operation.IsActive);
    }

    [Fact]
    public void Purge_plan_freezes_mixed_fixture_obligations_and_rejects_expiry_or_hold()
    {
        var registry = ReviewedDispositionRegistryV1.Create();
        var operation = LifecycleOperation.Admit(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            LifecycleOperationFamily.Termination, Guid.NewGuid(), registry, Now);
        operation.BeginTermination(Now.AddSeconds(1));
        foreach (var owner in registry.Inventory.Where(x => x.Capability.Key ==
                     ReviewedClosureRegistryV1.CapabilityKey))
            operation.RecordClosureAccepted(owner.ParticipantId);
        operation.CompleteClosureArchive(Now.AddSeconds(2), new string('a', 64));
        var set = RetentionDecisionSet.Create(Guid.NewGuid(), operation, "fixture", "v1",
            Now.AddSeconds(3), new string('b', 64), true);
        var categories = operation.Participants.Where(x => x.CapabilityKey ==
            ReviewedDispositionRegistryV1.CapabilityKey).OrderBy(x => x.OwnershipScope).ToArray();
        var records = categories.Select((category, index) => RetentionDecisionRecord.Create(
            set, category.OwnershipScope, category.ParticipantId, category.ContractVersion,
            Now.AddSeconds(3), Now.AddMinutes(1),
            index == 0 ? RetentionDecisionCode.Retain : RetentionDecisionCode.Purge,
            index == 0 ? null : Now.AddSeconds(3), null, "fixture")).ToArray();
        operation.MarkDispositionReady(Now.AddSeconds(3), set.SetHash);

        var plan = LifecyclePurgePlan.Create(Guid.NewGuid(), operation, set, records,
            Now.AddSeconds(4));
        Assert.Equal(4, plan.Entries.Count);
        Assert.Single(plan.Entries.Where(x => x.Decision == RetentionDecisionCode.Retain));
        Assert.Equal(3, plan.Entries.Count(x => x.Decision == RetentionDecisionCode.Purge));
        Assert.Equal(64, plan.PlanHash.Length);
        Assert.Equal(64, plan.BoundaryEvidenceHash(Now.AddSeconds(4)).Length);
        Assert.Throws<InvalidOperationException>(() => LifecyclePurgePlan.Create(Guid.NewGuid(),
            operation, set, records, Now.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() => LifecyclePurgePlan.Create(Guid.NewGuid(),
            operation, set, records[..^1], Now.AddSeconds(4)));

        foreach (var forbidden in new[] { RetentionDecisionCode.Held,
                     RetentionDecisionCode.Unknown, RetentionDecisionCode.Blocked })
        {
            var blocked = records.ToArray();
            blocked[1] = RetentionDecisionRecord.Create(set, categories[1].OwnershipScope,
                categories[1].ParticipantId, 1, Now.AddSeconds(3), Now.AddMinutes(1),
                forbidden, null, forbidden == RetentionDecisionCode.Held
                    ? "fixture-hold" : null, "fixture");
            Assert.Throws<InvalidOperationException>(() => LifecyclePurgePlan.Create(Guid.NewGuid(),
                operation, set, blocked, Now.AddSeconds(4)));
        }
        var future = records.ToArray();
        future[1] = RetentionDecisionRecord.Create(set, categories[1].OwnershipScope,
            categories[1].ParticipantId, 1, Now.AddSeconds(3), Now.AddMinutes(1),
            RetentionDecisionCode.Purge, Now.AddSeconds(10), null, "fixture");
        Assert.Throws<InvalidOperationException>(() => LifecyclePurgePlan.Create(Guid.NewGuid(),
            operation, set, future, Now.AddSeconds(4)));

        var entry = plan.Entries.First(x => x.Decision == RetentionDecisionCode.Purge);
        var commandId = PurgeMessageV1.CommandId(plan.Id, entry.ParticipantId,
            entry.Category, entry.ItemId);
        var command = new PurgeMessageV1(commandId, PurgeMessageKind.ParticipantPurge,
            operation.OrganisationId, operation.Id, operation.Revision + 1,
            operation.RegistryRevision, plan.Id, plan.PlanHash, set.Id, set.SetHash,
            entry.ParticipantId, ReviewedDispositionRegistryV1.CapabilityKey, entry.Category,
            entry.ItemId,
            commandId);
        Assert.Equal(command, PurgeMessageV1.Parse(command.CanonicalJson()));
        Assert.Throws<InvalidOperationException>(() => PurgeMessageV1.Parse(
            command.CanonicalJson().Replace("zeka-purge-message-v1", "zeka-purge-message-v2")));
        var progress = LifecyclePurgeParticipantProgress.Pending(plan, entry, command);
        PurgeReceiptV1 Failure(char proof, PurgeProgressState state) => new(Guid.NewGuid(),
            commandId, operation.OrganisationId, operation.Id, command.OperationRevision,
            operation.RegistryRevision, plan.Id, plan.PlanHash, set.Id, set.SetHash,
            entry.ParticipantId, ReviewedDispositionRegistryV1.CapabilityKey, entry.Category,
            entry.ItemId,
            commandId, state, new string(proof, 64), "synthetic-file-unavailable");
        var first = Failure('a', PurgeProgressState.FailedRetryable);
        Assert.True(progress.Record(first, plan, command.OperationRevision, Now.AddSeconds(5)));
        Assert.True(progress.Record(first, plan, command.OperationRevision, Now.AddSeconds(5)));
        Assert.False(progress.Record(first with { SafeFailureCode = "conflict" }, plan,
            command.OperationRevision, Now.AddSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => progress.Record(
            first with { ContractVersion = 2 }, plan, command.OperationRevision, Now.AddSeconds(5)));
        Assert.True(progress.Record(Failure('b', PurgeProgressState.FailedRetryable), plan,
            command.OperationRevision, Now.AddSeconds(6)));
        Assert.True(progress.Record(Failure('c', PurgeProgressState.InterventionRequired), plan,
            command.OperationRevision, Now.AddSeconds(7)));
        Assert.Equal(3, progress.Attempts);
        Assert.Equal(PurgeProgressState.InterventionRequired, progress.State);
        Assert.Throws<InvalidOperationException>(() => progress.Record(
            Failure('d', PurgeProgressState.Purged) with { SafeFailureCode = null },
            plan, command.OperationRevision, Now.AddSeconds(8)));
    }
}
