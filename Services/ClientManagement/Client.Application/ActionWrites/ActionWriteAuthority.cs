using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Application.ActionWrites;

public sealed record ActionTimeZone(string TimeZoneId, long ConfigVersion, DateTimeOffset ObservedAtUtc,
    TimeZoneInfo Zone);

public sealed record AssigneeDecision(string TargetViewScope, string DecisionVersion, DateTimeOffset ObservedAtUtc);

public sealed record ActionPreflight(int ClientId, int SocialCaseId);

public interface IActionAuthorityReads
{
    Task<ActionTimeZone> TimeZoneAsync(Guid organisationId, Guid actorSubjectId, Guid actorMembershipId,
        CancellationToken cancellationToken);
    Task<AssigneeDecision?> EligibilityAsync(Guid organisationId, Guid actorSubjectId, Guid actorMembershipId,
        Guid targetMembershipId, CancellationToken cancellationToken);
}

// Scoped to one operation. It never carries a remote authority decision across requests.
public sealed class ActionWriteAuthorityScope
{
    public DateOnly? LocalDay { get; private set; }
    public ActionTimeZone? TimeZone { get; private set; }
    public AssigneeDecision? Assignee { get; private set; }
    public Guid TargetMembershipId { get; private set; }
    private bool finalGuardArmed;
    private bool finalGuardUsed;

    public void SetTimeZone(ActionTimeZone zone, DateTimeOffset utcNow)
    {
        if (TimeZone is not null) throw new InvalidOperationException("Action timezone was already established.");
        TimeZone = zone;
        LocalDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone.Zone).DateTime);
    }

    public void SetAssignee(Guid targetMembershipId, AssigneeDecision decision)
    {
        if (TimeZone is null || targetMembershipId == Guid.Empty || Assignee is not null)
            throw new InvalidOperationException("Action authority is not established exactly once.");
        TargetMembershipId = targetMembershipId;
        Assignee = decision;
    }

    public void ArmFinalGuard()
    {
        if (Assignee is null || finalGuardArmed || finalGuardUsed)
            throw new TenantAccessException(AccessFailure.Unavailable);
        finalGuardArmed = true;
        finalGuardUsed = true;
    }

    public bool ConsumeFinalGuard()
    {
        var armed = finalGuardArmed;
        finalGuardArmed = false;
        return armed;
    }

    public void DemandTarget(Guid membershipId)
    {
        if (Assignee is null || TargetMembershipId == Guid.Empty
            || Assignee.TargetViewScope is not ("All" or "Assigned"))
            throw new TenantAccessException(AccessFailure.Unavailable);
        if (Assignee.TargetViewScope == "Assigned" && membershipId != TargetMembershipId)
            throw new ActionWriteConflictException("INVALID_ASSIGNEE");
    }

    public void ValidateFinal(DateTimeOffset databaseNow)
    {
        if (TimeZone is null || Assignee is null || LocalDay is null)
            throw new TenantAccessException(AccessFailure.Unavailable);
        if (!Fresh(Assignee.ObservedAtUtc, databaseNow) || !Fresh(TimeZone.ObservedAtUtc, databaseNow)
            || DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(databaseNow, TimeZone.Zone).DateTime) != LocalDay)
            throw new TenantAccessException(AccessFailure.Unavailable);
    }

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now) =>
        observed.Offset == TimeSpan.Zero && now - observed is var age
        && age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromSeconds(2);
}
