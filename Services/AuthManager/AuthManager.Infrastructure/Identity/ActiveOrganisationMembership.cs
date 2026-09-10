using AuthManager.Application.Authorization;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Identity;

public sealed class ActiveOrganisationMembership(AuthDbContext database) : IActiveOrganisationMembership
{
    public Task<bool> ExistsAsync(Guid organisationId, Guid membershipId, CancellationToken cancellationToken,
        bool requireActive = true) =>
        database.OrganisationMemberships.AsNoTracking().AnyAsync(m => m.Id == membershipId
            && m.OrganisationId == organisationId && (!requireActive || m.Status == MembershipStatus.Active), cancellationToken);
}
