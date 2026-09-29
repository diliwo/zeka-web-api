using AuthManager.Core.Lifecycle;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Application.Lifecycle;

public enum PurgeAdmissionStatus { Admitted, Replay, Blocked, Conflict, Unavailable }

public sealed record PurgeAdmissionResult(PurgeAdmissionStatus Status, Guid OperationId,
    long OperationRevision, Guid? PlanId = null, string? PlanHash = null);

public sealed record PurgeAdmissionRequest(Guid PlanId, Guid OperationId,
    Guid OrganisationId, long ExpectedOperationRevision, Guid ExpectedDecisionSetId,
    string ExpectedDecisionSetHash);

public enum PurgeReceiptStatus { Recorded, Replay, Conflict, Unavailable }

public sealed record PurgeReceiptResult(PurgeReceiptStatus Status, Guid OperationId,
    string Category, bool ExecutionComplete = false);

/// <summary>Implemented only by an isolated non-production harness, never by normal API DI.</summary>
public interface INonProductionPurgeCapability
{
    void Demand(Guid organisationId);
}

public interface ILifecyclePurgeStore
{
    Task<PurgeAdmissionResult> AdmitAsync(PurgeAdmissionRequest request,
        CancellationToken cancellationToken);
    Task<PurgeReceiptResult> RecordReceiptAsync(PurgeReceiptV1 receipt,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PurgeMessageV1>> ReadAdmittedMessagesAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken);
}

/// <summary>
/// Owner-local destructive participant contract. LIFE-04A supplies only isolated fixture
/// adapters; production service composition must not register an implementation.
/// </summary>
public interface INonProductionPurgeParticipant
{
    string ParticipantId { get; }
    Task AcceptStartAsync(PurgeCommandV1 start, CancellationToken cancellationToken);
    Task<PurgeParticipantReceiptV1> ExecuteAsync(PurgeCommandV1 command,
        CancellationToken cancellationToken);
}

/// <summary>No HTTP endpoint, scheduler or production service registration is provided.</summary>
public sealed class LifecyclePurgeCoordinator(
    ILifecyclePurgeStore store, INonProductionPurgeCapability fixtureCapability)
{
    public Task<PurgeAdmissionResult> AdmitAsync(PurgeAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(request.OrganisationId);
        return store.AdmitAsync(request, cancellationToken);
    }

    public Task<PurgeReceiptResult> RecordReceiptAsync(PurgeParticipantReceiptV1 receipt,
        CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(receipt.OrganisationId);
        return store.RecordReceiptAsync(new PurgeReceiptV1(receipt.ReceiptId,
            receipt.CommandMessageId, receipt.OrganisationId, receipt.TerminationOperationId,
            receipt.IrreversibleRevision, receipt.RegistryRevision, receipt.PlanId,
            receipt.PlanHash, receipt.DecisionSetId, receipt.DecisionSetHash,
            receipt.ParticipantId, receipt.CapabilityKey, receipt.Category, receipt.ItemId,
            receipt.IdempotencyId, (PurgeProgressState)receipt.State,
            receipt.EvidenceHash, receipt.SafeFailureCode, receipt.ContractVersion),
            cancellationToken);
    }

    public async Task<IReadOnlyList<PurgeCommandV1>> ReadAdmittedMessagesAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken = default)
    {
        fixtureCapability.Demand(organisationId);
        var messages = await store.ReadAdmittedMessagesAsync(operationId,
            organisationId, cancellationToken);
        return messages.Select(x => PurgeCommandV1.Parse(x.CanonicalJson())).ToArray();
    }
}
