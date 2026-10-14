using System.Data;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.TimeZones;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Identity;

/// <summary>One current tenant-scoped observation, without a positive cache.</summary>
public sealed class OrganisationTimeZoneResolver(AuthDbContext database, TimeProvider clock)
    : IOrganisationTimeZone
{
    public async Task<OrganisationTimeZoneDecision?> ResolveAsync(
        OrganisationTimeZoneRequest request, CancellationToken cancellationToken)
    {
        if (request.ContractVersion != 1 || request.OrganisationId == Guid.Empty
            || request.ActorSubjectId == Guid.Empty || request.ActorMembershipId == Guid.Empty)
            return null;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        var attempts = 0;
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            if (++attempts > 2)
                throw new InvalidOperationException("Time zone observation retry budget exceeded.");
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, token);
            await TenantContextInitializer.InitializeAsync(database, transaction, request.OrganisationId, token);
            var observedAt = clock.GetUtcNow();

            var organisation = await database.Organisations.AsNoTracking()
                .Where(o => o.Id == request.OrganisationId && o.Status == OrganisationStatus.Active)
                .Select(o => new { o.TimeZoneId, o.TimeZoneConfigVersion })
                .SingleOrDefaultAsync(token);
            if (organisation is null || organisation.TimeZoneId is null
                || organisation.TimeZoneConfigVersion <= 0)
                return null;
            if (!ConfigureTimeZoneApprovalVerifier.IsCanonicalIana(organisation.TimeZoneId))
                throw new InvalidOperationException("Stored organisation time zone is unresolvable.");

            var actorRows = await (
                from membership in database.OrganisationMemberships.AsNoTracking()
                join user in database.Users.AsNoTracking() on membership.UserId equals user.Id
                where membership.OrganisationId == request.OrganisationId
                    && membership.UserId == request.ActorSubjectId
                select new { membership.Id, MembershipStatus = membership.Status, UserStatus = user.Status,
                    user.EmailConfirmed, user.LockoutEnabled, user.LockoutEnd })
                .Take(2).ToListAsync(token);
            if (actorRows.Count != 1 || actorRows[0].Id != request.ActorMembershipId
                || actorRows[0].MembershipStatus != MembershipStatus.Active
                || actorRows[0].UserStatus != UserStatus.Active
                || !actorRows[0].EmailConfirmed
                || actorRows[0].LockoutEnabled && actorRows[0].LockoutEnd > observedAt)
                return null;

            await transaction.CommitAsync(token);
            return new OrganisationTimeZoneDecision(request.OrganisationId,
                organisation.TimeZoneId, organisation.TimeZoneConfigVersion, observedAt);
        }, deadline.Token);
    }
}
