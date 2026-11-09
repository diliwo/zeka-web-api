using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Application.ActionWrites;

// Shared only by fixed-case linked commands and B2's guarded case resolvers.
public sealed class LinkedInteractionExecutor(TenantOperation operation,
    ITenantTransactionExecutor transactions, PostCommitNotifications notifications,
    IActionWriteStore actionWrites, IActionAuthorityReads? authority = null,
    IActionWriteActivationGate? activation = null, ActionWriteAuthorityScope? actionScope = null,
    TimeProvider? clock = null)
{
    private static readonly RequiresTenantPermissionAttribute Edit =
        new("Clients.EditAll", "Clients.EditAssigned");
    private static readonly Meter AuthorityMeter = new("ClientManagement.ActionAuthority.v1");
    private static readonly Histogram<double> CombinedRemoteDuration =
        AuthorityMeter.CreateHistogram<double>("action_authority.combined_remote_duration_ms", "ms");

    public Task<RecordInteractionResult> ExecuteAsync(RecordInteractionCommand command,
        Func<CancellationToken, Task<RecordInteractionResult>> write, CancellationToken token) =>
        ExecuteCoreAsync(_ => Task.FromResult<RecordInteractionCommand?>(command),
            (_, _) => Task.FromResult(command), (_, t) => write(t), token);

    public Task<RecordInteractionResult> ExecuteAsync(
        Func<CancellationToken, Task<RecordInteractionCommand?>> recover,
        Func<DateOnly, CancellationToken, Task<RecordInteractionCommand>> resolve,
        CancellationToken token) =>
        ExecuteCoreAsync(recover, resolve,
            (command, t) => actionWrites.RecordAsync(command, operation.MembershipId, t), token);

    private async Task<RecordInteractionResult> ExecuteCoreAsync(
        Func<CancellationToken, Task<RecordInteractionCommand?>> recover,
        Func<DateOnly, CancellationToken, Task<RecordInteractionCommand>> resolve,
        Func<RecordInteractionCommand, CancellationToken, Task<RecordInteractionResult>> write,
        CancellationToken token)
    {
        var callerStarted = Stopwatch.GetTimestamp();
        await operation.AuthorizeAsync(Edit, token);
        var combinedRemote = Stopwatch.GetElapsedTime(callerStarted);
        (activation ?? throw new TenantAccessException(AccessFailure.Unavailable)).DemandActionWrite();
        if (authority is null || actionScope is null
            || !Guid.TryParse(operation.SubjectId, out var actorSubjectId) || actorSubjectId == Guid.Empty)
            throw new TenantAccessException(AccessFailure.Unavailable);

        if (!operation.AssignedOnly && await ReadSavedAsync() is { } savedAll)
            return savedAll;
        var zoneStarted = Stopwatch.GetTimestamp();
        ActionTimeZone zone;
        try
        {
            zone = await authority.TimeZoneAsync(operation.OrganisationId, actorSubjectId,
                operation.MembershipId, token);
        }
        catch
        {
            RecordRemote(combinedRemote + Stopwatch.GetElapsedTime(zoneStarted), "timezone_failure");
            throw;
        }
        combinedRemote += Stopwatch.GetElapsedTime(zoneStarted);
        actionScope.SetTimeZone(zone, (clock ?? TimeProvider.System).GetUtcNow());
        if (operation.AssignedOnly && await ReadSavedAsync() is { } savedAssigned)
            return savedAssigned;

        var command = await transactions.ExecuteOnceAsync(async t =>
        {
            var current = await resolve(actionScope.LocalDay
                ?? throw new TenantAccessException(AccessFailure.Unavailable), t);
            if (current.Action is null || current.Action.AssigneeMembershipId == Guid.Empty)
                throw new ActionWriteConflictException("INVALID_ACTION");
            await actionWrites.PreflightAsync(current, t);
            return current;
        }, token);
        var assigneeStarted = Stopwatch.GetTimestamp();
        AssigneeDecision? decision;
        try
        {
            decision = await authority.EligibilityAsync(operation.OrganisationId, actorSubjectId,
                operation.MembershipId, command.Action!.AssigneeMembershipId, token);
        }
        catch
        {
            RecordRemote(combinedRemote + Stopwatch.GetElapsedTime(assigneeStarted), "eligibility_failure");
            throw;
        }
        combinedRemote += Stopwatch.GetElapsedTime(assigneeStarted);
        RecordRemote(combinedRemote, decision is null ? "ineligible" : "eligible");
        if (decision is null) throw new ActionWriteConflictException("INVALID_ASSIGNEE");
        actionScope.SetAssignee(command.Action!.AssigneeMembershipId, decision);

        RecordInteractionResult result;
        try
        {
            actionScope.ArmFinalGuard();
            result = await transactions.ExecuteOrdinaryOnceAsync(t => write(command, t), token);
        }
        catch (TenantCommitIndeterminateException)
        {
            // Reconcile the immutable attempted command; never replay the write or authorities.
            var reconciled = await transactions.ExecuteOrdinaryOnceAsync(
                t => actionWrites.ReconcileAsync(command, operation.MembershipId, t), token);
            if (reconciled is null) throw;
            result = reconciled;
        }
        await notifications.PublishAsync(token);
        return result;

        Task<RecordInteractionResult?> ReadSavedAsync() =>
            transactions.ExecuteOrdinaryOnceAsync(async t =>
            {
                var anchored = await recover(t);
                return anchored is null ? null
                    : await actionWrites.TrySavedResultAsync(anchored, operation.MembershipId, t);
            }, token);
    }

    private static void RecordRemote(TimeSpan duration, string outcome) =>
        CombinedRemoteDuration.Record(duration.TotalMilliseconds,
            new KeyValuePair<string, object?>("outcome", outcome));
}
