using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace AuthManager.Infrastructure.Identity;

public sealed class CurrentTenantAccessResolver(AuthDbContext database, TimeProvider clock) : ICurrentTenantAccess
{
    public async Task<CurrentTenantAccess> ResolveAsync(Guid authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (authenticatedSubjectId == Guid.Empty || selectedOrganisationId == Guid.Empty)
            return CurrentTenantAccess.Denied(now);

        // The caller's one deadline also bounds execution-strategy backoff and every complete attempt.
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async attemptCancellation =>
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, attemptCancellation);
            var result = await ResolveInCurrentTransactionAsync(authenticatedSubjectId, selectedOrganisationId, now,
                attemptCancellation);
            if (result.Outcome == TenantAccessOutcome.Authorized) await transaction.CommitAsync(attemptCancellation);
            return result;
        }, cancellationToken);
    }

    // Eligible choices and selected access share one policy and a caller-owned repeatable-read
    // snapshot. This never starts or commits a nested transaction or grants authority from an ID.
    internal async Task<CurrentTenantAccess> ResolveInCurrentTransactionAsync(Guid authenticatedSubjectId,
        Guid selectedOrganisationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var transaction = database.Database.CurrentTransaction
            ?? throw new InvalidOperationException("CURRENT_TENANT_AUTHORITY_TRANSACTION_REQUIRED");
        await TenantContextInitializer.InitializeAsync(
            database, transaction, selectedOrganisationId, cancellationToken);

        // A repeatable-read database observation avoids assembling an authorization decision from stale claims
        // or independently cached organisation, membership, role, and explicit-grant records.
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
            select new { membership.Id, RoleId = role.Id, Role = role.Code,
                MembershipVersion = membership.ConcurrencyVersion, OrganisationVersion = organisation.ConcurrencyVersion,
                organisation.DisplayName, organisation.TimeZoneId, organisation.TimeZoneConfigVersion })
            .Take(2).ToListAsync(cancellationToken);

        if (matches.Count != 1 || TenantPermissions.Resolve(matches[0].RoleId, matches[0].Role) is not { } permissions)
            return CurrentTenantAccess.Denied(now);

        var match = matches[0];
        var explicitGrants = await database.MembershipPermissionGrants.AsNoTracking()
            .Where(grant => grant.OrganisationId == selectedOrganisationId
                && grant.OrganisationMembershipId == match.Id
                && grant.RevokedAtUtc == null)
            .OrderBy(grant => grant.PermissionKey)
            .ThenBy(grant => grant.Id)
            .Select(grant => new { grant.Id, grant.PermissionKey, grant.ConcurrencyVersion })
            .ToListAsync(cancellationToken);
        if (explicitGrants.Any(grant =>
                !TenantPermissions.IsExplicitGrantAllowed(match.Role, grant.PermissionKey)))
            return CurrentTenantAccess.Denied(now);
        var effectivePermissions = permissions.Concat(explicitGrants.Select(grant => grant.PermissionKey))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var grantVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            explicitGrants.Select(grant => $"{grant.Id:D}:{grant.ConcurrencyVersion}:{grant.PermissionKey}")))));
        return new CurrentTenantAccess(TenantAccessOutcome.Authorized, selectedOrganisationId, match.Id,
            effectivePermissions,
            $"v2:{match.OrganisationVersion}:{match.MembershipVersion}:{match.Role}:{grantVersion}", now,
            match.DisplayName, match.TimeZoneId, match.TimeZoneConfigVersion);
    }

}
