using AuthManager.Core.Lifecycle;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Application.Lifecycle;

public enum ClosureProgressStatus
{
    Progressed,
    Replay,
    AwaitingParticipants,
    Archived,
    RecoveryStarted,
    Recovered,
    Conflict,
    Rejected,
    Unavailable
}

public sealed record ClosureProgressResult(
    ClosureProgressStatus Status,
    Guid OperationId,
    long OperationRevision,
    string? FailureCode = null);

public interface ILifecycleClosureStore
{
    Task<ClosureProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken);
    Task<ClosureProgressResult> AcceptCompletedAsync(OrganisationClosureParticipantCompletedV1 completed,
        CancellationToken cancellationToken);
    Task<ClosureProgressResult> AcceptFailedAsync(OrganisationClosureParticipantFailedV1 failed,
        CancellationToken cancellationToken);
    Task<ClosureProgressResult> BeginRecoveryAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken);
    Task<ClosureProgressResult> AcceptReleasedAsync(OrganisationClosureFenceReleasedV1 released,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> RecoverableAsync(Guid organisationId, CancellationToken cancellationToken);
    Task<ClosureProgressResult> ResumeAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken);
}

public sealed class LifecycleClosureCoordinator(ILifecycleClosureStore store)
{
    public Task<ClosureProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken = default) =>
        Valid(operationId, organisationId)
            ? store.BeginAsync(operationId, organisationId, cancellationToken)
            : Task.FromResult(new ClosureProgressResult(ClosureProgressStatus.Rejected, operationId, 0));

    public Task<ClosureProgressResult> ReceiveAsync(OrganisationClosureParticipantCompletedV1 completed,
        CancellationToken cancellationToken = default) =>
        store.AcceptCompletedAsync(completed ?? throw new ArgumentNullException(nameof(completed)), cancellationToken);

    public Task<ClosureProgressResult> ReceiveAsync(OrganisationClosureParticipantFailedV1 failed,
        CancellationToken cancellationToken = default) =>
        store.AcceptFailedAsync(failed ?? throw new ArgumentNullException(nameof(failed)), cancellationToken);

    public Task<ClosureProgressResult> RecoverBeforeArchiveAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken = default) =>
        Valid(operationId, organisationId)
            ? store.BeginRecoveryAsync(operationId, organisationId, cancellationToken)
            : Task.FromResult(new ClosureProgressResult(ClosureProgressStatus.Rejected, operationId, 0));

    public Task<ClosureProgressResult> ReceiveAsync(OrganisationClosureFenceReleasedV1 released,
        CancellationToken cancellationToken = default) =>
        store.AcceptReleasedAsync(released ?? throw new ArgumentNullException(nameof(released)), cancellationToken);

    public async Task<IReadOnlyList<ClosureProgressResult>> RecoverAsync(Guid organisationId,
        CancellationToken cancellationToken = default)
    {
        if (organisationId == Guid.Empty) return [];
        var operations = await store.RecoverableAsync(organisationId, cancellationToken);
        var results = new List<ClosureProgressResult>(operations.Count);
        foreach (var operation in operations.Order())
            results.Add(await store.ResumeAsync(operation, organisationId, cancellationToken));
        return results;
    }

    private static bool Valid(Guid operationId, Guid organisationId) =>
        operationId != Guid.Empty && organisationId != Guid.Empty;
}
