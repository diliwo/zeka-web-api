using AuthManager.Core.Lifecycle;

namespace AuthManager.Application.Lifecycle;

public enum DispositionEvaluationStatus { Ready, Blocked, Replay, Conflict, Unavailable }

public sealed record DispositionCategory(string Category, string ParticipantId, int ContractVersion);

public sealed record RetentionEvaluationRequest(
    Guid EvaluationId,
    Guid OrganisationId,
    Guid TerminationOperationId,
    long OperationRevision,
    Guid RegistryRevision,
    string InventoryHash,
    string DispositionInventoryHash,
    DateTimeOffset EvaluatedAt,
    IReadOnlyList<DispositionCategory> Categories);

public sealed record RetentionCategoryDecision(
    string Category,
    string PolicyId,
    string PolicyVersion,
    DateTimeOffset DecidedAt,
    DateTimeOffset ValidUntil,
    RetentionDecisionCode Decision,
    DateTimeOffset? EligibleAt,
    string? HoldReference,
    string ReasonCode);

public sealed record RetentionEvaluationResponse(IReadOnlyList<RetentionCategoryDecision> Decisions);

public sealed record DispositionEvaluationResult(
    DispositionEvaluationStatus Status,
    Guid OperationId,
    long OperationRevision,
    string? DecisionSetHash = null)
{
    public IReadOnlyList<string> RetainedExceptions { get; init; } = [];
    public IReadOnlyList<string> PurgeEligibleCategories { get; init; } = [];
    public IReadOnlyList<string> FuturePurgeCategories { get; init; } = [];
}

/// <summary>
/// Policy-neutral Application port. No production implementation or scheduler is registered.
/// The provider must return one current versioned decision per frozen category.
/// </summary>
public interface IRetentionPolicy
{
    Task<RetentionEvaluationResponse?> EvaluateAsync(RetentionEvaluationRequest request,
        CancellationToken cancellationToken);
}

public interface ILifecycleDispositionStore
{
    Task<RetentionEvaluationRequest?> ReadRequestAsync(Guid evaluationId, Guid operationId,
        Guid organisationId, DateTimeOffset evaluatedAt, CancellationToken cancellationToken);
    Task<DispositionEvaluationResult> CommitAsync(RetentionEvaluationRequest request,
        RetentionEvaluationResponse response, CancellationToken cancellationToken);
}

/// <summary>Internal deterministic orchestration only; intentionally absent from production DI.</summary>
public sealed class LifecycleDispositionCoordinator(
    ILifecycleDispositionStore store, IRetentionPolicy policy, TimeProvider clock)
{
    public async Task<DispositionEvaluationResult> EvaluateAsync(Guid evaluationId, Guid operationId,
        Guid organisationId, CancellationToken cancellationToken = default)
    {
        if (evaluationId == Guid.Empty || operationId == Guid.Empty || organisationId == Guid.Empty)
            return new(DispositionEvaluationStatus.Conflict, operationId, 0);
        var request = await store.ReadRequestAsync(evaluationId, operationId, organisationId,
            clock.GetUtcNow(), cancellationToken);
        if (request is null)
            return new(DispositionEvaluationStatus.Blocked, operationId, 0);
        var response = await policy.EvaluateAsync(request, cancellationToken);
        return response is null
            ? new(DispositionEvaluationStatus.Unavailable, operationId, request.OperationRevision)
            : await store.CommitAsync(request, response, cancellationToken);
    }
}
