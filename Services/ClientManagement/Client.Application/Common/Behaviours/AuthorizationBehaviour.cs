using System.Reflection;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MediatR;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.ActionWrites;

namespace ClientManagement.Application.Common.Behaviours;

public sealed class AuthorizationBehaviour<TRequest, TResponse>(TenantOperation operation,
    ITenantTransactionExecutor transactions, PostCommitNotifications notifications,
    IActionWriteStore actionWrites, IActionAuthorityReads? authority = null,
    IActionWriteActivationGate? activation = null, ActionWriteAuthorityScope? actionScope = null,
    TimeProvider? clock = null)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    private static readonly Meter AuthorityMeter = new("ClientManagement.ActionAuthority.v1");
    private static readonly Histogram<double> CombinedRemoteDuration = AuthorityMeter.CreateHistogram<double>(
        "action_authority.combined_remote_duration_ms", "ms");

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var policy = typeof(TRequest).GetCustomAttribute<RequiresTenantPermissionAttribute>()
            ?? throw new TenantAccessException(AccessFailure.Denied);
        var actionSensitive = request is RecordInteractionCommand { Action: not null }
            or ActionChangeCommand { Kind: ActionChangeKind.Reassign };
        var callerStarted = Stopwatch.GetTimestamp();
        await operation.AuthorizeAsync(policy, cancellationToken);
        var combinedRemote = Stopwatch.GetElapsedTime(callerStarted);
        if (actionSensitive)
        {
            // Production remains closed. Synthetic integration tests may open only the bounded path.
            (activation ?? throw new TenantAccessException(AccessFailure.Unavailable)).DemandActionWrite();
            if (authority is null || actionScope is null) throw new TenantAccessException(AccessFailure.Unavailable);
            if (!Guid.TryParse(operation.SubjectId, out var actorSubjectId) || actorSubjectId == Guid.Empty)
                throw new TenantAccessException(AccessFailure.Unavailable);
            // An EditAll retry can return the exact saved result without consulting a now-closed
            // case or a subsequently unavailable authority. The closure fence and current dossier
            // visibility still apply. Assigned visibility needs the authoritative local day first.
            if (!operation.AssignedOnly && await ReadSavedResultAsync() is TResponse savedAll)
                return savedAll;
            var zoneStarted = Stopwatch.GetTimestamp();
            ActionTimeZone zone;
            try
            {
                zone = await authority.TimeZoneAsync(operation.OrganisationId, actorSubjectId,
                    operation.MembershipId, cancellationToken);
            }
            catch
            {
                CombinedRemoteDuration.Record((combinedRemote + Stopwatch.GetElapsedTime(zoneStarted)).TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", "timezone_failure"));
                throw;
            }
            combinedRemote += Stopwatch.GetElapsedTime(zoneStarted);
            actionScope.SetTimeZone(zone, (clock ?? TimeProvider.System).GetUtcNow());
            if (operation.AssignedOnly && await ReadSavedResultAsync() is TResponse savedAssigned)
                return savedAssigned;
            var target = request switch
            {
                RecordInteractionCommand record => record.Action!.AssigneeMembershipId,
                ActionChangeCommand change => change.NewAssigneeMembershipId ?? Guid.Empty,
                _ => Guid.Empty
            };
            if (target == Guid.Empty) throw new ActionWriteConflictException("INVALID_ACTION");
            // A short guarded RLS read proves dossier authority before semantic ineligibility is exposed.
            await transactions.ExecuteOnceAsync(async token =>
            {
                switch (request)
                {
                    case RecordInteractionCommand record:
                        await actionWrites.PreflightAsync(record, token); break;
                    case ActionChangeCommand change:
                        await actionWrites.PreflightAsync(change, token); break;
                }
                return true;
            }, cancellationToken);
            // Last remote call; the ordinary write attempt below cannot replay it.
            var assigneeStarted = Stopwatch.GetTimestamp();
            AssigneeDecision? decision;
            try
            {
                decision = await authority.EligibilityAsync(operation.OrganisationId, actorSubjectId,
                    operation.MembershipId, target, cancellationToken);
            }
            catch
            {
                CombinedRemoteDuration.Record((combinedRemote + Stopwatch.GetElapsedTime(assigneeStarted)).TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", "eligibility_failure"));
                throw;
            }
            combinedRemote += Stopwatch.GetElapsedTime(assigneeStarted);
            CombinedRemoteDuration.Record(combinedRemote.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", decision is null ? "ineligible" : "eligible"));
            if (decision is null) throw new ActionWriteConflictException("INVALID_ASSIGNEE");
            actionScope.SetAssignee(target, decision);
        }
        var ordinaryMutation = ClientRequestActivityClassifier.Classify(typeof(TRequest))
            == ClientRequestActivity.OrdinaryOrganisationMutation;
        TResponse result;
        if (ClientRequestActivityClassifier.RequiresNonReplayingOrdinary(typeof(TRequest)))
        {
            try
            {
                if (actionSensitive) actionScope!.ArmFinalGuard();
                result = await transactions.ExecuteOrdinaryOnceAsync(_ => next(), cancellationToken);
            }
            catch (TenantCommitIndeterminateException)
            {
                // Fresh, guarded read only. Never replay the mutation or a remote authority decision.
                object? reconciled = request switch
                {
                    RecordInteractionCommand command => (object?)await transactions.ExecuteOrdinaryOnceAsync(
                        token => actionWrites.ReconcileAsync(command, operation.MembershipId, token), cancellationToken),
                    ActionChangeCommand command => (object?)await transactions.ExecuteOrdinaryOnceAsync(
                        token => actionWrites.ReconcileAsync(command, operation.MembershipId, token), cancellationToken),
                    _ => null
                };
                if (reconciled is not TResponse typed) throw;
                result = typed;
            }
        }
        else if (ordinaryMutation)
            result = await transactions.ExecuteOrdinaryAsync(_ => next(), cancellationToken);
        else
            result = await transactions.ExecuteAsync(_ => next(), cancellationToken);
        await notifications.PublishAsync(cancellationToken);
        return result;

        async Task<object?> ReadSavedResultAsync() => request switch
        {
            RecordInteractionCommand command => await transactions.ExecuteOrdinaryOnceAsync(
                token => actionWrites.TrySavedResultAsync(command, operation.MembershipId, token), cancellationToken),
            ActionChangeCommand command => await transactions.ExecuteOrdinaryOnceAsync(
                token => actionWrites.TrySavedResultAsync(command, operation.MembershipId, token), cancellationToken),
            _ => null
        };
    }
}
