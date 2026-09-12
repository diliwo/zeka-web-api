using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Identity;

public sealed class CurrentTenantAccessResolver(AuthDbContext database, TimeProvider clock) : ICurrentTenantAccess
{
    public async Task<CurrentTenantAccess> ResolveAsync(Guid authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (authenticatedSubjectId == Guid.Empty || selectedOrganisationId == Guid.Empty)
            return CurrentTenantAccess.Denied(now);

        // A single database observation avoids assembling an authorization decision from stale claims
        // or independently cached organisation, membership, and role records.
        var matches = await (
            from membership in database.OrganisationMemberships.AsNoTracking()
            join organisation in database.Organisations on membership.OrganisationId equals organisation.Id
            join user in database.Users on membership.UserId equals user.Id
            join role in database.PermissionSets on membership.PermissionSetId equals role.Id
            where membership.UserId == authenticatedSubjectId
                && membership.OrganisationId == selectedOrganisationId
                && membership.Status == MembershipStatus.Active
                && organisation.Status == OrganisationStatus.Active
                && user.Status == UserStatus.Active
                && user.EmailConfirmed
                && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now)
                && role.IsSystem
            select new { membership.Id, Role = role.Code,
                MembershipVersion = membership.ConcurrencyVersion, OrganisationVersion = organisation.ConcurrencyVersion })
            .Take(2).ToListAsync(cancellationToken);

        if (matches.Count != 1 || TenantPermissions.Resolve(matches[0].Role) is not { } permissions)
            return CurrentTenantAccess.Denied(now);

        var match = matches[0];
        return new CurrentTenantAccess(TenantAccessOutcome.Authorized, selectedOrganisationId, match.Id,
            permissions.Order(StringComparer.Ordinal).ToArray(),
            $"v1:{match.OrganisationVersion}:{match.MembershipVersion}:{match.Role}", now);
    }
}
