namespace AuthManager.Application.Authorization;

public enum TenantAccessOutcome { Authorized, Denied, Unavailable }

public sealed record CurrentTenantAccess(
    TenantAccessOutcome Outcome,
    Guid OrganisationId,
    Guid OrganisationMembershipId,
    IReadOnlyCollection<string> EffectivePermissionCodes,
    string DecisionVersion,
    DateTimeOffset ObservedAtUtc)
{
    public static CurrentTenantAccess Denied(DateTimeOffset now) =>
        new(TenantAccessOutcome.Denied, Guid.Empty, Guid.Empty, Array.Empty<string>(), string.Empty, now);
}

/// <summary>The subject must come from the authenticated delivery boundary, never a request body.</summary>
public interface ICurrentTenantAccess
{
    Task<CurrentTenantAccess> ResolveAsync(Guid authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken = default);
}
