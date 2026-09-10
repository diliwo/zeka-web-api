using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Application.Staffs;

public interface IStaffProjectionOutbox
{
    void Stage(StaffMember staff, Team team);
    Task DispatchAsync(CancellationToken cancellationToken);
}

public interface IStaffMembershipLink
{
    Task<bool> IsActiveAsync(Guid organisationId, Guid membershipId, CancellationToken cancellationToken);
}
