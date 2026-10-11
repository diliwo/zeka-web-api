using System.Data;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Identity;

/// <summary>A fresh, tenant-scoped observation; no dossier assignment or positive decision cache.</summary>
public sealed class AssigneeEligibilityResolver(AuthDbContext database, TimeProvider clock) : IAssigneeEligibility
{
    public async Task<AssigneeEligibilityDecision?> ResolveAsync(
        AssigneeEligibilityRequest request, CancellationToken cancellationToken)
    {
        if (request.ContractVersion != 1 || request.OrganisationId == Guid.Empty
            || request.ActorSubjectId == Guid.Empty || request.ActorMembershipId == Guid.Empty
            || request.TargetMembershipId == Guid.Empty)
            return null;

        var observedAt = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await TenantContextInitializer.InitializeAsync(database, transaction, request.OrganisationId, cancellationToken);

        var organisation = await database.Organisations.AsNoTracking()
            .Where(o => o.Id == request.OrganisationId && o.Status == OrganisationStatus.Active)
            .Select(o => new { o.ConcurrencyVersion }).SingleOrDefaultAsync(cancellationToken);
        if (organisation is null) return null;

        // Count raw rows before joins: malformed linked data must not hide a duplicate membership.
        var actorMembershipIds = await database.OrganisationMemberships.AsNoTracking()
            .Where(m => m.OrganisationId == request.OrganisationId && m.UserId == request.ActorSubjectId)
            .Select(m => m.Id).Take(2).ToListAsync(cancellationToken);
        if (actorMembershipIds.Count != 1 || actorMembershipIds[0] != request.ActorMembershipId)
            return null;

        var actors = await (
            from membership in database.OrganisationMemberships.AsNoTracking()
            join user in database.Users.AsNoTracking() on membership.UserId equals user.Id
            join role in database.PermissionSets.AsNoTracking() on membership.PermissionSetId equals role.Id
            where membership.OrganisationId == request.OrganisationId
                && membership.UserId == request.ActorSubjectId
            select new MembershipObservation(membership.Id, membership.UserId, membership.Status,
                membership.ConcurrencyVersion, user.Status, user.EmailConfirmed,
                user.LockoutEnabled, user.LockoutEnd, role.Code, role.IsSystem))
            .Take(2).ToListAsync(cancellationToken);
        if (actors.Count != 1 || !IsCurrent(actors[0], observedAt)) return null;

        var targets = await (
            from membership in database.OrganisationMemberships.AsNoTracking()
            join user in database.Users.AsNoTracking() on membership.UserId equals user.Id
            join role in database.PermissionSets.AsNoTracking() on membership.PermissionSetId equals role.Id
            where membership.OrganisationId == request.OrganisationId
                && membership.Id == request.TargetMembershipId
            select new MembershipObservation(membership.Id, membership.UserId, membership.Status,
                membership.ConcurrencyVersion, user.Status, user.EmailConfirmed,
                user.LockoutEnabled, user.LockoutEnd, role.Code, role.IsSystem))
            .Take(2).ToListAsync(cancellationToken);
        if (targets.Count != 1 || !IsCurrent(targets[0], observedAt)) return null;
        var targetMembershipCount = await database.OrganisationMemberships.AsNoTracking()
            .Where(m => m.OrganisationId == request.OrganisationId && m.UserId == targets[0].UserId)
            .Take(2).CountAsync(cancellationToken);
        if (targetMembershipCount != 1) return null;

        var actorPermissions = TenantPermissions.Resolve(actors[0].RoleCode);
        var targetPermissions = TenantPermissions.Resolve(targets[0].RoleCode);
        if (actorPermissions is null || targetPermissions is null
            || !actorPermissions.Contains("Clients.EditAll") && !actorPermissions.Contains("Clients.EditAssigned")
            || !targetPermissions.Contains("Clients.ViewAll") && !targetPermissions.Contains("Clients.ViewAssigned"))
            return null;

        // The current explicit-grant catalogue permits only Organisations.Export for Admin.
        // A malformed or unknown active grant makes this authority observation invalid.
        var grants = await database.MembershipPermissionGrants.AsNoTracking()
            .Where(g => g.OrganisationId == request.OrganisationId && g.RevokedAtUtc == null
                && (g.OrganisationMembershipId == actors[0].MembershipId
                    || g.OrganisationMembershipId == targets[0].MembershipId))
            .Select(g => new { g.OrganisationMembershipId, g.PermissionKey }).ToListAsync(cancellationToken);
        if (grants.Any(g => !TenantPermissions.IsExplicitGrantAllowed(
                g.OrganisationMembershipId == actors[0].MembershipId ? actors[0].RoleCode : targets[0].RoleCode,
                g.PermissionKey))) return null;

        await transaction.CommitAsync(cancellationToken);
        return new AssigneeEligibilityDecision(
            targetPermissions.Contains("Clients.ViewAll") ? "All" : "Assigned",
            $"v1:{organisation.ConcurrencyVersion}:{actors[0].Version}:{targets[0].Version}:{actors[0].RoleCode}:{targets[0].RoleCode}",
            observedAt);
    }

    private static bool IsCurrent(MembershipObservation membership, DateTimeOffset now) =>
        membership.Status == MembershipStatus.Active && membership.UserStatus == UserStatus.Active
        && membership.EmailConfirmed && membership.IsSystemRole
        && (!membership.LockoutEnabled || membership.LockoutEnd is null || membership.LockoutEnd <= now);

    private sealed record MembershipObservation(Guid MembershipId, Guid UserId, MembershipStatus Status,
        uint Version, UserStatus UserStatus, bool EmailConfirmed, bool LockoutEnabled,
        DateTimeOffset? LockoutEnd, string RoleCode, bool IsSystemRole);
}
