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

        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await InitializeTenantContextAsync(database, transaction, selectedOrganisationId, cancellationToken);

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
            select new { membership.Id, Role = role.Code,
                MembershipVersion = membership.ConcurrencyVersion, OrganisationVersion = organisation.ConcurrencyVersion })
            .Take(2).ToListAsync(cancellationToken);

        if (matches.Count != 1 || TenantPermissions.Resolve(matches[0].Role) is not { } permissions)
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
        if (explicitGrants.Any(grant => !TenantPermissions.IsKnown(grant.PermissionKey)))
            return CurrentTenantAccess.Denied(now);
        var effectivePermissions = permissions.Concat(explicitGrants.Select(grant => grant.PermissionKey))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var grantVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|',
            explicitGrants.Select(grant => $"{grant.Id:D}:{grant.ConcurrencyVersion}:{grant.PermissionKey}")))));
        await transaction.CommitAsync(cancellationToken);
        return new CurrentTenantAccess(TenantAccessOutcome.Authorized, selectedOrganisationId, match.Id,
            effectivePermissions,
            $"v2:{match.OrganisationVersion}:{match.MembershipVersion}:{match.Role}:{grantVersion}", now);
    }

    private static async Task InitializeTenantContextAsync(
        AuthDbContext database,
        IDbContextTransaction transaction,
        Guid organisationId,
        CancellationToken cancellationToken)
    {
        if (!database.Database.IsNpgsql())
        {
            return;
        }

        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
        {
            throw new InvalidOperationException("Authorization tenant context initialization failed.");
        }
    }
}
