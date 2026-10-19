namespace AuthManager.Application.Authorization;

public enum EligibleOrganisationsOutcome { Success, Unauthenticated, Overflow, Unavailable }
public sealed record EligibleOrganisation(Guid OrganisationId, string DisplayName);
public sealed record EligibleOrganisationsResult(EligibleOrganisationsOutcome Outcome,
    IReadOnlyList<EligibleOrganisation> Organisations);

public interface IEligibleOrganisations
{
    Task<EligibleOrganisationsResult> ResolveAsync(Guid authenticatedSubjectId,
        CancellationToken cancellationToken = default);
}
