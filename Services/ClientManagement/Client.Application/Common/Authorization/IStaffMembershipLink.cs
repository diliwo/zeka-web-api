namespace ClientManagement.Application.Common.Authorization;

public interface IStaffMembershipLink
{
    Task<bool> VerifyAsync(Guid organisationId, Guid membershipId, bool requireActive, CancellationToken cancellationToken);
}
