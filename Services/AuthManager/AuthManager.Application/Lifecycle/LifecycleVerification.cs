using Zeka.Lifecycle.Contracts;

namespace AuthManager.Application.Lifecycle;

public enum VerificationStatus { Recorded, Replay, Conflict, Incomplete, Finalized, Unavailable }

public sealed record VerificationResult(VerificationStatus Status, Guid OperationId,
    string? Category = null);

/// <summary>Only a fixture composition may implement this. Production DI/HTTP has no verifier route.</summary>
public interface INonProductionPurgeVerifier
{
    string ParticipantId { get; }
    Task<VerifyPurgeReceiptV1> ObserveAsync(VerifyPurgeCommandV1 command,
        CancellationToken cancellationToken);
}

/// <summary>Isolated fixture writer gate; owner writers and finalizer share one operation fence.</summary>
public interface INonProductionFixtureWriteFence
{
    Task<INonProductionFixtureWriteFenceLease> HoldAsync(Guid organisationId,
        Guid operationId, CancellationToken cancellationToken);
    void DemandHeld(INonProductionFixtureWriteFenceLease lease, Guid organisationId,
        Guid operationId);
}

public interface INonProductionFixtureWriteFenceLease : IAsyncDisposable
{
    bool IsHeldFor(Guid organisationId, Guid operationId);
    void Seal();
}

public interface ILifecycleVerificationStore
{
    Task<IReadOnlyList<VerifyPurgeCommandV1>> IssueCommandsAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken);
    Task<VerificationResult> RecordObservedAsync(VerifyPurgeCommandV1 command,
        INonProductionPurgeVerifier authenticatedOwner,
        CancellationToken cancellationToken);
    Task<VerificationResult> FinalizeAsync(Guid operationId, Guid organisationId,
        long expectedOperationRevision, CancellationToken cancellationToken,
        INonProductionFixtureWriteFenceLease? heldFixtureFence = null);
}

/// <summary>Coordinates only through owner contracts, without any service-specific storage access.</summary>
public sealed class LifecycleVerificationCoordinator(ILifecycleVerificationStore store,
    INonProductionPurgeCapability fixtureCapability,
    IReadOnlyCollection<INonProductionPurgeVerifier> registeredOwnerVerifiers,
    INonProductionFixtureWriteFence fixtureWriteFence)
{
    public async Task<IReadOnlyList<VerifyPurgeCommandV1>> IssueCommandsAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(organisationId);
        return await store.IssueCommandsAsync(operationId, organisationId, cancellationToken);
    }

    public async Task<VerificationResult> ObserveAsync(VerifyPurgeCommandV1 command,
        CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(command.OrganisationId);
        var matching = registeredOwnerVerifiers.Where(x => x.ParticipantId == command.ParticipantId)
            .ToArray();
        if (matching.Length != 1)
            return new VerificationResult(VerificationStatus.Conflict,
                command.TerminationOperationId, command.Category);
        // The participant principal comes from the trusted fixture registry, never a caller field.
        return await store.RecordObservedAsync(command, matching[0],
            cancellationToken);
    }

    /// <summary>Re-observes every owner immediately before the guarded terminal transition.</summary>
    public async Task<VerificationResult> ReobserveAndFinalizeAsync(Guid operationId,
        Guid organisationId, long expectedOperationRevision,
        CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(organisationId);
        await using var fence = await fixtureWriteFence.HoldAsync(organisationId,
            operationId, cancellationToken);
        var commands = await store.IssueCommandsAsync(operationId, organisationId,
            cancellationToken);
        if (commands.Count == 0 || registeredOwnerVerifiers.Count != commands
                .Select(x => x.ParticipantId).Distinct(StringComparer.Ordinal).Count()
            || registeredOwnerVerifiers.GroupBy(x => x.ParticipantId, StringComparer.Ordinal)
                .Any(x => x.Count() != 1))
            return new VerificationResult(VerificationStatus.Incomplete, operationId);
        foreach (var command in commands)
        {
            var recorded = await ObserveAsync(command, cancellationToken);
            if (recorded.Status is not (VerificationStatus.Recorded or VerificationStatus.Replay))
                return recorded;
        }
        var result = await store.FinalizeAsync(operationId, organisationId,
            expectedOperationRevision, cancellationToken, fence);
        if (result.Status == VerificationStatus.Finalized)
            fence.Seal();
        return result;
    }
}
