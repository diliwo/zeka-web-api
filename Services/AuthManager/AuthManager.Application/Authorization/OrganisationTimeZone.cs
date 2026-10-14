using System.Text.Json.Serialization;

namespace AuthManager.Application.Authorization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OrganisationTimeZoneRequest(
    int ContractVersion, Guid OrganisationId, Guid ActorSubjectId, Guid ActorMembershipId);

public sealed record OrganisationTimeZoneDecision(
    Guid OrganisationId, string TimeZoneId, long ConfigVersion, DateTimeOffset ObservedAtUtc);

public interface IOrganisationTimeZone
{
    Task<OrganisationTimeZoneDecision?> ResolveAsync(
        OrganisationTimeZoneRequest request, CancellationToken cancellationToken);
}
