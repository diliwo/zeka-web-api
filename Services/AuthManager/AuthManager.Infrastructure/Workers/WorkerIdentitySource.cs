using AuthManager.Application.Workers;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Workers;

// Only the admitted, in-process M1 operator path can activate these passwordless machine identities.
public sealed class WorkerIdentitySource(AuthDbContext database, UserManager<User> users, TimeProvider clock)
{
    public async Task ValidateOwnerAsync(WorkerRotationCaller caller, Guid organisationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // The organisation row is the stable pair serialization and lifecycle fence, not an operation-ID lock.
        if (database.Database.IsNpgsql())
        {
            await database.Organisations.FromSqlInterpolated($"SELECT * FROM \"Organisations\" WHERE \"Id\" = {organisationId} FOR UPDATE").LoadAsync(ct);
            await database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {caller.SubjectId} FOR UPDATE").LoadAsync(ct);
            await database.OrganisationMemberships.FromSqlInterpolated($"SELECT * FROM \"OrganisationMemberships\" WHERE \"OrganisationId\" = {organisationId} AND \"UserId\" = {caller.SubjectId} FOR UPDATE").LoadAsync(ct);
            await database.BrowserSessionFamilies.FromSqlInterpolated($"SELECT * FROM \"BrowserSessionFamilies\" WHERE \"Id\" = {caller.SessionId} FOR UPDATE").LoadAsync(ct);
            await database.PermissionSets.FromSqlInterpolated($"SELECT * FROM \"PermissionSets\" WHERE \"Id\" = {PermissionSet.OrganisationOwnerId} FOR SHARE").LoadAsync(ct);
        }
        var organisation = await database.Organisations.SingleOrDefaultAsync(x => x.Id == organisationId, ct);
        var owner = await database.Users.SingleOrDefaultAsync(x => x.Id == caller.SubjectId, ct);
        var membership = await database.OrganisationMemberships.SingleOrDefaultAsync(x => x.OrganisationId == organisationId && x.UserId == caller.SubjectId, ct);
        var role = await database.PermissionSets.SingleOrDefaultAsync(x => x.Id == PermissionSet.OrganisationOwnerId, ct);
        var family = await database.BrowserSessionFamilies.SingleOrDefaultAsync(x => x.Id == caller.SessionId, ct);
        if (organisation is null || organisation.Status != OrganisationStatus.Active || organisation.OwnerUserId != caller.SubjectId
            || owner is null || owner.Status != UserStatus.Active || !owner.EmailConfirmed || string.IsNullOrWhiteSpace(owner.SecurityStamp)
            || owner.LockoutEnabled && owner.LockoutEnd > now
            || membership is null || membership.Status != MembershipStatus.Active || membership.PermissionSetId != PermissionSet.OrganisationOwnerId
            || role is null || !role.IsSystem || role.Code != "Owner"
            || family is null || family.UserId != caller.SubjectId || family.SecurityStamp != owner.SecurityStamp
            || family.RevokedAtUtc is not null || family.IdleExpiresAtUtc <= now || family.AbsoluteExpiresAtUtc <= now)
            throw new WorkerRotationException("WORKER_ROTATION_CURRENT_OWNER_REJECTED");
    }

    public async Task<(Guid SubjectId, Guid MembershipId)> CreateAsync(string profile, Guid organisationId, CancellationToken ct)
    {
        if (!WorkerProfiles.All.Contains(profile, StringComparer.Ordinal)) throw new WorkerRotationException("WORKER_PROFILE_REJECTED");
        var role = await database.PermissionSets.SingleOrDefaultAsync(x => x.Id == PermissionSet.StaffProjectionWorkerId, ct);
        if (role is null)
        {
            if (await database.PermissionSets.AnyAsync(x => x.Code == PermissionSet.StaffProjectionWorkerCode, ct))
                throw new WorkerRotationException("WORKER_CATALOGUE_CONFLICT");
            database.PermissionSets.Add(PermissionSet.CreateStaffProjectionWorker());
        }
        else if (!role.IsSystem || role.Code != PermissionSet.StaffProjectionWorkerCode)
            throw new WorkerRotationException("WORKER_CATALOGUE_REJECTED");
        var now = clock.GetUtcNow();
        var suffix = Guid.NewGuid().ToString("N");
        var name = $"m1-{profile}-{suffix}";
        var user = User.Create($"{name}@workers.m1.invalid", name, "M1", profile, now);
        user.EmailConfirmed = true;
        user.ActivateConfirmedIdentity(now);
        var created = await users.CreateAsync(user); // No password, sign-in, refresh row or owner permission.
        if (!created.Succeeded || string.IsNullOrWhiteSpace(user.SecurityStamp))
            throw new WorkerRotationException("WORKER_IDENTITY_CREATE_REJECTED");
        var membership = OrganisationMembership.Create(Guid.NewGuid(), organisationId, user.Id, PermissionSet.StaffProjectionWorkerId, now);
        database.OrganisationMemberships.Add(membership);
        await database.SaveChangesAsync(ct);
        return (user.Id, membership.Id);
    }

    public async Task<User> ValidateWorkerAsync(Guid subjectId, Guid membershipId, Guid organisationId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (database.Database.IsNpgsql())
        {
            await database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {subjectId} FOR UPDATE").LoadAsync(ct);
            await database.OrganisationMemberships.FromSqlInterpolated($"SELECT * FROM \"OrganisationMemberships\" WHERE \"UserId\" = {subjectId} FOR SHARE").LoadAsync(ct);
            await database.PermissionSets.FromSqlInterpolated($"SELECT * FROM \"PermissionSets\" WHERE \"Id\" = {PermissionSet.StaffProjectionWorkerId} FOR SHARE").LoadAsync(ct);
        }
        var user = await database.Users.SingleOrDefaultAsync(x => x.Id == subjectId, ct);
        var memberships = await database.OrganisationMemberships.Where(x => x.UserId == subjectId).ToListAsync(ct);
        var membership = memberships.SingleOrDefault(x => x.Id == membershipId);
        var role = await database.PermissionSets.SingleOrDefaultAsync(x => x.Id == PermissionSet.StaffProjectionWorkerId, ct);
        if (user is null || user.Status != UserStatus.Active || !user.EmailConfirmed || user.PasswordHash is not null
            || string.IsNullOrWhiteSpace(user.SecurityStamp) || user.LockoutEnabled && user.LockoutEnd > now
            || memberships.Count != 1 || membership is null || membership.OrganisationId != organisationId
            || membership.Status != MembershipStatus.Active || membership.PermissionSetId != PermissionSet.StaffProjectionWorkerId
            || role is null || !role.IsSystem || role.Code != PermissionSet.StaffProjectionWorkerCode
            || TenantPermissions.Resolve(role.Id, role.Code) is not { Count: 1 } resolved
            || !resolved.Contains("TeamConfiguration.ManageStaffProfiles")
            || await database.UserRoles.AnyAsync(x => x.UserId == subjectId, ct)
            || await database.UserClaims.AnyAsync(x => x.UserId == subjectId, ct)
            || await database.MembershipPermissionGrants.AnyAsync(x => x.OrganisationMembershipId == membershipId && x.RevokedAtUtc == null, ct)
            || await database.BrowserRefreshCredentials.Join(database.BrowserSessionFamilies.Where(x => x.UserId == subjectId),
                row => row.FamilyId, family => family.Id, (row, family) => row.Hash).AnyAsync(ct))
            throw new WorkerRotationException("WORKER_CURRENT_AUTHORITY_REJECTED");
        return user;
    }
}
