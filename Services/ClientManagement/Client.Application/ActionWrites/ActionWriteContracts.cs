using ClientManagement.Application.Common.Authorization;
using MediatR;

namespace ClientManagement.Application.ActionWrites;

public sealed record FollowUpActionInput(string Purpose, DateOnly DueLocalDate, Guid AssigneeMembershipId);

[RequiresTenantPermission("Clients.EditAll", "Clients.EditAssigned")]
public sealed record RecordInteractionCommand(int ClientId, int SocialCaseId, DateTime OccurredAtUtc,
    string Note, string IdempotencyKey, FollowUpActionInput? Action = null) : IRequest<RecordInteractionResult>;

public sealed record RecordInteractionResult(int InteractionId, int Revision, int? FollowUpActionId);

public enum ActionChangeKind { Complete, Reassign, ChangeDueDate }

[RequiresTenantPermission("Clients.EditAll", "Clients.EditAssigned")]
public sealed record ActionChangeCommand(int ActionId, int ExpectedVersion, string IdempotencyKey,
    ActionChangeKind Kind, Guid? NewAssigneeMembershipId = null, DateOnly? NewDueLocalDate = null)
    : IRequest<ActionChangeResult>;

public sealed record ActionChangeResult(string State, int Version, int ChangeId);

public sealed class ActionWriteConflictException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public interface IActionWriteStore
{
    Task<RecordInteractionResult> RecordAsync(RecordInteractionCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken);
    Task<ActionChangeResult> ChangeAsync(ActionChangeCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken);
    Task<RecordInteractionResult?> ReconcileAsync(RecordInteractionCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken);
    Task<ActionChangeResult?> ReconcileAsync(ActionChangeCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken);
}

// Production implementation is intentionally closed until AssigneeEligibility.v1 + timezone integration.
public interface IActionWriteActivationGate
{
    void DemandActionWrite();
}

public sealed class ClosedActionWriteActivationGate : IActionWriteActivationGate
{
    public void DemandActionWrite() => throw new TenantAccessException(AccessFailure.Unavailable);
}

public sealed class RecordInteractionHandler(IActionWriteStore store, TenantOperation operation,
    IActionWriteActivationGate gate) : IRequestHandler<RecordInteractionCommand, RecordInteractionResult>
{
    public Task<RecordInteractionResult> Handle(RecordInteractionCommand request, CancellationToken cancellationToken)
    {
        if (request.Action is not null) gate.DemandActionWrite();
        return store.RecordAsync(request, operation.MembershipId, cancellationToken);
    }
}

public sealed class ActionChangeHandler(IActionWriteStore store, TenantOperation operation,
    IActionWriteActivationGate gate) : IRequestHandler<ActionChangeCommand, ActionChangeResult>
{
    public Task<ActionChangeResult> Handle(ActionChangeCommand request, CancellationToken cancellationToken)
    {
        gate.DemandActionWrite();
        return store.ChangeAsync(request, operation.MembershipId, cancellationToken);
    }
}
