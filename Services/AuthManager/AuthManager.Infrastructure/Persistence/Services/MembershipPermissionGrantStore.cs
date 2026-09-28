using System.Data;
using System.Data.Common;
using System.Text.Json;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AuthManager.Infrastructure.Persistence.Services;

public sealed class MembershipPermissionGrantStore(
    DbContextOptions<AuthDbContext> options,
    TimeProvider clock) : IMembershipPermissionGrantStore
{
    private const int MaximumAttempts = 3;

    public Task<MembershipPermissionGrantResult> GrantAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request, grant: true, cancellationToken);

    public Task<MembershipPermissionGrantResult> RevokeAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken) =>
        ExecuteAsync(request, grant: false, cancellationToken);

    private async Task<MembershipPermissionGrantResult> ExecuteAsync(
        MembershipPermissionGrantRequest request,
        bool grant,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            await using var database = new AuthDbContext(options);
            try
            {
                return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await database.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);
                    await TenantContextInitializer.InitializeAsync(
                        database, transaction, request.OrganisationId, cancellationToken);

                    if (!StringComparer.Ordinal.Equals(
                            request.PermissionKey, TenantPermissions.OrganisationExport)
                        || !await IsAuthorizedOwnerAsync(database, request, clock.GetUtcNow(), cancellationToken)
                        || !await TargetIsEligibleAdminAsync(database, request, grant, cancellationToken))
                    {
                        return new MembershipPermissionGrantResult(MembershipPermissionGrantStatus.Denied);
                    }

                    var active = await database.MembershipPermissionGrants.SingleOrDefaultAsync(
                        value => value.OrganisationMembershipId == request.TargetMembershipId
                            && value.PermissionKey == request.PermissionKey
                            && value.RevokedAtUtc == null,
                        cancellationToken);

                    if (grant)
                    {
                        if (active is not null)
                        {
                            await transaction.CommitAsync(cancellationToken);
                            return new MembershipPermissionGrantResult(
                                MembershipPermissionGrantStatus.AlreadyGranted,
                                active.Id);
                        }

                        var now = clock.GetUtcNow();
                        active = MembershipPermissionGrant.Create(
                            Guid.NewGuid(),
                            request.OrganisationId,
                            request.TargetMembershipId,
                            request.PermissionKey,
                            request.ActorMembershipId,
                            request.ActorSubjectId,
                            now);
                        database.MembershipPermissionGrants.Add(active);
                        database.AuditEntries.Add(CreateAudit(active, request, "MembershipPermission.Granted", now));
                        await database.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new MembershipPermissionGrantResult(MembershipPermissionGrantStatus.Granted, active.Id);
                    }

                    if (active is null)
                    {
                        var historicalId = await database.MembershipPermissionGrants.AsNoTracking()
                            .Where(value => value.OrganisationMembershipId == request.TargetMembershipId
                                && value.PermissionKey == request.PermissionKey)
                            .OrderByDescending(value => value.GrantedAtUtc)
                            .Select(value => (Guid?)value.Id)
                            .FirstOrDefaultAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return new MembershipPermissionGrantResult(
                            MembershipPermissionGrantStatus.AlreadyRevoked,
                            historicalId);
                    }

                    var revokedAt = clock.GetUtcNow();
                    if (!active.Revoke(request.ActorMembershipId, request.ActorSubjectId, revokedAt))
                    {
                        return new MembershipPermissionGrantResult(
                            MembershipPermissionGrantStatus.AlreadyRevoked,
                            active.Id);
                    }

                    database.AuditEntries.Add(CreateAudit(active, request, "MembershipPermission.Revoked", revokedAt));
                    await database.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new MembershipPermissionGrantResult(MembershipPermissionGrantStatus.Revoked, active.Id);
                });
            }
            catch (Exception exception) when (IsRetryable(exception))
            {
                // A competing grant/revoke owns the semantic tuple. A fresh serializable
                // attempt observes and deterministically returns that durable outcome.
            }
        }

        return new MembershipPermissionGrantResult(MembershipPermissionGrantStatus.Unavailable);
    }

    private static async Task<bool> IsAuthorizedOwnerAsync(
        AuthDbContext database,
        MembershipPermissionGrantRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await (
            from membership in database.OrganisationMemberships.AsNoTracking()
            join organisation in database.Organisations.AsNoTracking()
                on membership.OrganisationId equals organisation.Id
            join user in database.Users.AsNoTracking()
                on membership.UserId equals user.Id
            where membership.Id == request.ActorMembershipId
                && membership.OrganisationId == request.OrganisationId
                && membership.UserId == request.ActorSubjectId
                && membership.PermissionSetId == PermissionSet.OrganisationOwnerId
                && membership.Status == MembershipStatus.Active
                && organisation.Status == OrganisationStatus.Active
                && user.Status == UserStatus.Active
                && user.EmailConfirmed
                && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now)
            select membership.Id).AnyAsync(cancellationToken);
    }

    private static Task<bool> TargetIsEligibleAdminAsync(
        AuthDbContext database,
        MembershipPermissionGrantRequest request,
        bool requireActive,
        CancellationToken cancellationToken) =>
        database.OrganisationMemberships.AsNoTracking().AnyAsync(
            membership => membership.Id == request.TargetMembershipId
                && membership.OrganisationId == request.OrganisationId
                && membership.PermissionSetId == PermissionSet.OrganisationAdministratorId
                && (!requireActive || membership.Status == MembershipStatus.Active),
            cancellationToken);

    private static AuditEntry CreateAudit(
        MembershipPermissionGrant grant,
        MembershipPermissionGrantRequest request,
        string action,
        DateTimeOffset occurredAtUtc) =>
        AuditEntry.Create(
            request.ActorSubjectId,
            request.OrganisationId,
            action,
            nameof(MembershipPermissionGrant),
            grant.Id.ToString("D"),
            "Succeeded",
            occurredAtUtc,
            request.CorrelationId,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["actorMembershipId"] = request.ActorMembershipId.ToString("D"),
                ["targetMembershipId"] = request.TargetMembershipId.ToString("D"),
                ["permissionKey"] = request.PermissionKey
            }));

    private static bool IsRetryable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbUpdateConcurrencyException
                || current is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure
                })
            {
                return true;
            }
        }

        return false;
    }

}
