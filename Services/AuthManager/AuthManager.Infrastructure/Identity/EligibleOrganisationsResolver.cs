using System.Data;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Identity;

public sealed class EligibleOrganisationsResolver(AuthDbContext database, TimeProvider clock) : IEligibleOrganisations
{
    public Task<EligibleOrganisationsResult> ResolveAsync(Guid authenticatedSubjectId,
        CancellationToken cancellationToken = default)
        => database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (authenticatedSubjectId == Guid.Empty)
                return new EligibleOrganisationsResult(EligibleOrganisationsOutcome.Unauthenticated, []);
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken);
            var now = clock.GetUtcNow();
            // User eligibility and choices belong to one database snapshot, without selected-tenant context.
            var eligibleUser = await database.Users.AsNoTracking().AnyAsync(user =>
                user.Id == authenticatedSubjectId && user.Status == UserStatus.Active && user.EmailConfirmed
                && (!user.LockoutEnabled || user.LockoutEnd == null || user.LockoutEnd <= now), cancellationToken);
            if (!eligibleUser)
                return new EligibleOrganisationsResult(EligibleOrganisationsOutcome.Unauthenticated, []);
            // These tables are not subject-RLS protected: this predicate is the subject boundary.
            var choices = await (from membership in database.OrganisationMemberships.AsNoTracking()
                join organisation in database.Organisations.AsNoTracking() on membership.OrganisationId equals organisation.Id
                where membership.UserId == authenticatedSubjectId && membership.Status == MembershipStatus.Active
                    && organisation.Status == OrganisationStatus.Active
                select new EligibleOrganisation(organisation.Id, organisation.DisplayName))
                .Take(101).ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (choices.Count > 100)
                return new EligibleOrganisationsResult(EligibleOrganisationsOutcome.Overflow, []);
            return new EligibleOrganisationsResult(EligibleOrganisationsOutcome.Success,
                choices.OrderBy(choice => choice.DisplayName, StringComparer.Ordinal)
                    .ThenBy(choice => choice.OrganisationId.ToString("D"), StringComparer.Ordinal).ToArray());
        });
}
