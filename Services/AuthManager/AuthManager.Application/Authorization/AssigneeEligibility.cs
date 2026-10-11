using System.Text.Json.Serialization;

namespace AuthManager.Application.Authorization;

/// <summary>ClientManagement's asserted actor identity is checked here, not authenticated as a user token.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssigneeEligibilityRequest(
    int ContractVersion,
    Guid OrganisationId,
    Guid ActorSubjectId,
    Guid ActorMembershipId,
    Guid TargetMembershipId);

public sealed record AssigneeEligibilityDecision(
    string TargetViewScope,
    string DecisionVersion,
    DateTimeOffset ObservedAtUtc);

/// <summary>Returns null for every semantic denial, without exposing which membership failed.</summary>
public interface IAssigneeEligibility
{
    Task<AssigneeEligibilityDecision?> ResolveAsync(
        AssigneeEligibilityRequest request, CancellationToken cancellationToken);
}
