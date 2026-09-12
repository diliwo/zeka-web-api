using Zeka.Extensions.EventBus;

namespace Zeka.Contracts.Staff.V1;

// Complete snapshot; higher revisions supersede lower ones, including deletion tombstones.
public sealed record StaffProjectionChangedV1(Guid OrganisationId, Guid OrganisationMembershipId,
    long Revision, bool Active, string FirstName, string LastName, string UserName,
    string TeamName, string TeamAcronym) : Event;
