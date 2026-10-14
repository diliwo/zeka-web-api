using System.Text.Json;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using ActionHistory = ClientManagement.Core.Entities.ActionChange;

namespace ClientManagement.Infrastructure.Persistence;

internal sealed class ActionWriteStore(ApplicationDbContext database, IClientParentLock locks,
    IActionWriteActivationGate activation, ActionWriteAuthorityScope authority) : IActionWriteStore
{
    private const string RecordOperation = "RecordInteraction.v1";
    private const string ChangeOperation = "ActionChange.v1";

    public async Task<RecordInteractionResult?> TrySavedResultAsync(RecordInteractionCommand request,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        await DemandDossierAsync(request.ClientId, cancellationToken);
        return await FindRecordReceiptAsync(request, actorMembershipId, cancellationToken);
    }

    public async Task<ActionChangeResult?> TrySavedResultAsync(ActionChangeCommand request,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var clientId = await locks.ResolveActionClientAsync(request.ActionId, cancellationToken);
        if (clientId is null) throw new TenantAccessException(AccessFailure.Denied);
        await DemandDossierAsync(clientId.Value, cancellationToken);
        return await FindChangeReceiptAsync(request, actorMembershipId, cancellationToken);
    }

    public async Task<ActionPreflight> PreflightAsync(RecordInteractionCommand request,
        CancellationToken cancellationToken)
    {
        await DemandDossierAsync(request.ClientId, cancellationToken);
        await DemandPreflightCaseAsync(request.ClientId, request.SocialCaseId, cancellationToken);
        return new ActionPreflight(request.ClientId, request.SocialCaseId);
    }

    public async Task<ActionPreflight> PreflightAsync(ActionChangeCommand request,
        CancellationToken cancellationToken)
    {
        var clientId = await locks.ResolveActionClientAsync(request.ActionId, cancellationToken);
        if (clientId is null) throw new TenantAccessException(AccessFailure.Denied);
        await DemandDossierAsync(clientId.Value, cancellationToken);
        var action = await database.Visible<FollowUpAction>().AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.ActionId && a.ClientId == clientId.Value,
                cancellationToken) ?? throw new TenantAccessException(AccessFailure.Denied);
        await DemandPreflightCaseAsync(clientId.Value, action.SocialCaseId, cancellationToken);
        return new ActionPreflight(clientId.Value, action.SocialCaseId);
    }

    private async Task DemandPreflightCaseAsync(int clientId, int expectedCaseId, CancellationToken token)
    {
        var day = authority.LocalDay?.ToDateTime(TimeOnly.MinValue)
            ?? throw new TenantAccessException(AccessFailure.Unavailable);
        var current = await database.SocialCases.AsNoTracking().Where(x => x.ClientId == clientId)
            .Where(CurrentAssignment.ActiveOn(day)).Take(2).ToListAsync(token);
        if (current.Count == 0) throw new ActionWriteConflictException("CONTEXT_REQUIRED");
        if (current.Count != 1 || current[0].Id != expectedCaseId)
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        if (!await database.SocialWorkers.AnyAsync(w => w.Id == current[0].SocialWorkerId
                && !w.Softdelete && w.OrganisationMembershipId != Guid.Empty, token))
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
    }

    public async Task<RecordInteractionResult> RecordAsync(RecordInteractionCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        ValidateKey(request.IdempotencyKey, actorMembershipId);
        if (request.ClientId <= 0 || request.SocialCaseId <= 0 || request.OccurredAtUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ActionWriteConflictException("INVALID_INTERACTION");
        if (request.Action is not null)
        {
            activation.DemandActionWrite();
            if (string.IsNullOrWhiteSpace(request.Action.Purpose) || request.Action.Purpose.Length > 500
                || request.Action.AssigneeMembershipId == Guid.Empty || request.Action.DueLocalDate == default)
                throw new ActionWriteConflictException("INVALID_ACTION");
        }

        await locks.LockAsync(request.ClientId, cancellationToken);
        await DemandDossierAsync(request.ClientId, cancellationToken);
        var duplicate = await FindRecordReceiptAsync(request, actorMembershipId, cancellationToken);
        if (duplicate is not null) return duplicate;

        var current = await DemandCurrentCaseAsync(request.ClientId, request.SocialCaseId, cancellationToken);
        if (request.Action is not null)
            await DemandTargetAsync(current.SocialWorkerId, cancellationToken);
        var interaction = new Interaction
        {
            ClientId = request.ClientId,
            SocialCaseId = current.Id,
            AuthorMembershipId = actorMembershipId,
            OccurredAtUtc = request.OccurredAtUtc,
            Note = request.Note
        };
        database.Interactions.Add(interaction);
        await database.SaveChangesAsync(cancellationToken);

        FollowUpAction? action = null;
        if (request.Action is not null)
        {
            action = new FollowUpAction
            {
                ClientId = request.ClientId,
                SocialCaseId = current.Id,
                InteractionId = interaction.Id,
                Purpose = request.Action.Purpose,
                DueLocalDate = request.Action.DueLocalDate,
                AssigneeMembershipId = request.Action.AssigneeMembershipId,
                CreatorMembershipId = actorMembershipId
            };
            database.FollowUpActions.Add(action);
            await database.SaveChangesAsync(cancellationToken);
            database.ActionChanges.Add(new ActionHistory
            {
                FollowUpActionId = action.Id,
                ActorMembershipId = actorMembershipId,
                Operation = "Create",
                PriorVersion = 0,
                NewVersion = 1,
                PriorState = string.Empty,
                NewState = "Open",
                PriorAssigneeMembershipId = Guid.Empty,
                NewAssigneeMembershipId = action.AssigneeMembershipId,
                PriorDueLocalDate = DateOnly.MinValue,
                NewDueLocalDate = action.DueLocalDate,
                ChangedAtUtc = DateTime.UtcNow
            });
            await database.SaveChangesAsync(cancellationToken);
        }

        database.IdempotencyReceipts.Add(new IdempotencyReceipt
        {
            ActorMembershipId = actorMembershipId,
            Operation = RecordOperation,
            ClientKey = request.IdempotencyKey,
            ClientId = request.ClientId,
            CanonicalMetadata = RecordMetadata(request),
            InteractionId = interaction.Id,
            FollowUpActionId = action?.Id,
            ResultVersion = 1,
            // No automated purge until a separately approved sensitive-history retention policy exists.
            RetainUntilUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
        });
        await database.SaveChangesAsync(cancellationToken);
        return new RecordInteractionResult(interaction.Id, 1, action?.Id);
    }

    public async Task<ActionChangeResult> ChangeAsync(ActionChangeCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken)
    {
        activation.DemandActionChange(request.Kind);
        ValidateKey(request.IdempotencyKey, actorMembershipId);
        if (request.ActionId <= 0 || request.ExpectedVersion <= 0 || !Enum.IsDefined(request.Kind)
            || request.Kind == ActionChangeKind.Complete
                && (request.NewAssigneeMembershipId.HasValue || request.NewDueLocalDate.HasValue)
            || request.Kind == ActionChangeKind.Reassign
                && (!request.NewAssigneeMembershipId.HasValue || request.NewAssigneeMembershipId.Value == Guid.Empty
                    || request.NewDueLocalDate.HasValue)
            || request.Kind == ActionChangeKind.ChangeDueDate
                && (!request.NewDueLocalDate.HasValue || request.NewDueLocalDate.Value == default
                    || request.NewAssigneeMembershipId.HasValue))
            throw new ActionWriteConflictException("INVALID_ACTION_CHANGE");

        var clientId = await locks.ResolveActionClientAsync(request.ActionId, cancellationToken);
        if (clientId is null) throw new TenantAccessException(AccessFailure.Denied);
        await locks.LockAsync(clientId.Value, cancellationToken);
        await DemandDossierAsync(clientId.Value, cancellationToken);
        var duplicate = await FindChangeReceiptAsync(request, actorMembershipId, cancellationToken);
        if (duplicate is not null) return duplicate;

        var candidate = await database.FollowUpActions.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == request.ActionId && a.ClientId == clientId.Value, cancellationToken)
            ?? throw new TenantAccessException(AccessFailure.Denied);
        var currentCase = await DemandCurrentCaseAsync(clientId.Value, candidate.SocialCaseId, cancellationToken);
        if (request.Kind == ActionChangeKind.Reassign)
            await DemandTargetAsync(currentCase.SocialWorkerId, cancellationToken);
        await locks.LockActionAsync(request.ActionId, cancellationToken);
        var action = await database.FollowUpActions.SingleOrDefaultAsync(a => a.Id == request.ActionId
            && a.ClientId == clientId.Value, cancellationToken)
            ?? throw new TenantAccessException(AccessFailure.Denied);
        if (action.Version != request.ExpectedVersion || action.State != "Open")
            throw new ActionWriteConflictException("VERSION_CONFLICT");
        var beforeVersion = action.Version;
        var beforeState = action.State;
        var beforeAssignee = action.AssigneeMembershipId;
        var beforeDue = action.DueLocalDate;
        switch (request.Kind)
        {
            case ActionChangeKind.Complete: action.State = "Completed"; break;
            case ActionChangeKind.Reassign: action.AssigneeMembershipId = request.NewAssigneeMembershipId!.Value; break;
            case ActionChangeKind.ChangeDueDate: action.DueLocalDate = request.NewDueLocalDate!.Value; break;
        }
        action.Version++;
        if (await database.SaveChangesAsync(cancellationToken) != 1)
            throw new ActionWriteConflictException("VERSION_CONFLICT");
        var change = new ActionHistory
        {
            FollowUpActionId = action.Id,
            ActorMembershipId = actorMembershipId,
            Operation = request.Kind.ToString(),
            PriorVersion = beforeVersion,
            NewVersion = action.Version,
            PriorState = beforeState,
            NewState = action.State,
            PriorAssigneeMembershipId = beforeAssignee,
            NewAssigneeMembershipId = action.AssigneeMembershipId,
            PriorDueLocalDate = beforeDue,
            NewDueLocalDate = action.DueLocalDate,
            ChangedAtUtc = DateTime.UtcNow
        };
        database.ActionChanges.Add(change);
        await database.SaveChangesAsync(cancellationToken);
        database.IdempotencyReceipts.Add(new IdempotencyReceipt
        {
            ActorMembershipId = actorMembershipId,
            Operation = ChangeOperation,
            ClientKey = request.IdempotencyKey,
            ClientId = clientId.Value,
            CanonicalMetadata = ChangeMetadata(request),
            FollowUpActionId = action.Id,
            ActionChangeId = change.Id,
            ResultVersion = action.Version,
            RetainUntilUtc = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)
        });
        await database.SaveChangesAsync(cancellationToken);
        return new ActionChangeResult(action.State, action.Version, change.Id);
    }

    public Task<RecordInteractionResult?> ReconcileAsync(RecordInteractionCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken) => FindRecordReceiptAsync(request, actorMembershipId, cancellationToken);

    public Task<ActionChangeResult?> ReconcileAsync(ActionChangeCommand request, Guid actorMembershipId,
        CancellationToken cancellationToken) => FindChangeReceiptAsync(request, actorMembershipId, cancellationToken);

    private async Task<RecordInteractionResult?> FindRecordReceiptAsync(RecordInteractionCommand request,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var receipt = await database.Visible<IdempotencyReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorMembershipId == actorMembershipId
                && x.Operation == RecordOperation && x.ClientKey == request.IdempotencyKey, cancellationToken);
        if (receipt is null) return null;
        var saved = receipt.InteractionId is int id
            ? await database.Visible<Interaction>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id,
                cancellationToken) : null;
        if (receipt.ClientId != request.ClientId || receipt.CanonicalMetadata != RecordMetadata(request)
            || saved is null || !StringComparer.Ordinal.Equals(saved.Note, request.Note))
            throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        return new RecordInteractionResult(saved.Id, receipt.ResultVersion, receipt.FollowUpActionId);
    }

    private async Task<ActionChangeResult?> FindChangeReceiptAsync(ActionChangeCommand request,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var receipt = await database.Visible<IdempotencyReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorMembershipId == actorMembershipId
                && x.Operation == ChangeOperation && x.ClientKey == request.IdempotencyKey, cancellationToken);
        if (receipt is null) return null;
        var change = receipt.ActionChangeId is int id
            ? await database.Visible<ActionHistory>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id,
                cancellationToken) : null;
        if (receipt.CanonicalMetadata != ChangeMetadata(request) || receipt.FollowUpActionId != request.ActionId
            || change is null)
            throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        return new ActionChangeResult(change.NewState, receipt.ResultVersion, change.Id);
    }

    private async Task DemandDossierAsync(int clientId, CancellationToken cancellationToken)
    {
        if (!await database.Visible<Client>().AnyAsync(x => x.Id == clientId, cancellationToken))
            throw new TenantAccessException(AccessFailure.Denied);
    }

    private async Task<SocialCase> DemandCurrentCaseAsync(int clientId, int expectedCaseId,
        CancellationToken cancellationToken)
    {
        var day = authority.LocalDay?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today;
        var cases = await database.SocialCases.AsNoTracking()
            .Where(x => x.ClientId == clientId).Where(CurrentAssignment.ActiveOn(day))
            .Take(2).ToListAsync(cancellationToken);
        if (cases.Count == 0) throw new ActionWriteConflictException("CONTEXT_REQUIRED");
        if (cases.Count != 1 || cases[0].Id != expectedCaseId)
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        await locks.LockWorkerAsync(cases[0].SocialWorkerId, cancellationToken);
        // READ COMMITTED: both the case and assignment predicate must be checked after parent and worker locks.
        database.ChangeTracker.Clear();
        await DemandDossierAsync(clientId, cancellationToken);
        var current = await database.SocialCases.AsNoTracking()
            .Where(x => x.ClientId == clientId).Where(CurrentAssignment.ActiveOn(day))
            .Take(2).ToListAsync(cancellationToken);
        if (current.Count != 1 || current[0].Id != expectedCaseId)
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        if (!await database.SocialWorkers.AnyAsync(w => w.Id == current[0].SocialWorkerId
                && !w.Softdelete && w.OrganisationMembershipId != Guid.Empty, cancellationToken))
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        return current[0];
    }

    private async Task DemandTargetAsync(int socialWorkerId, CancellationToken token)
    {
        var membership = await database.SocialWorkers.Where(w => w.Id == socialWorkerId && !w.Softdelete)
            .Select(w => w.OrganisationMembershipId).SingleOrDefaultAsync(token);
        if (membership == Guid.Empty) throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        authority.DemandTarget(membership);
    }


    private static void ValidateKey(string key, Guid actorMembershipId)
    {
        if (actorMembershipId == Guid.Empty || string.IsNullOrWhiteSpace(key) || key.Length > 128)
            throw new ActionWriteConflictException("INVALID_IDEMPOTENCY_KEY");
    }

    private static string RecordMetadata(RecordInteractionCommand request) => JsonSerializer.Serialize(new
    {
        request.ClientId, request.SocialCaseId, request.OccurredAtUtc, request.Action
    });

    private static string ChangeMetadata(ActionChangeCommand request) => JsonSerializer.Serialize(new
    {
        request.ActionId, request.ExpectedVersion, request.Kind,
        request.NewAssigneeMembershipId, request.NewDueLocalDate
    });
}
