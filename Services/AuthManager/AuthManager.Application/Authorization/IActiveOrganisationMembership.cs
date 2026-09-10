namespace AuthManager.Application.Authorization;

public interface IActiveOrganisationMembership
{
    Task<bool> ExistsAsync(Guid organisationId, Guid membershipId, CancellationToken cancellationToken,
        bool requireActive = true);
}
