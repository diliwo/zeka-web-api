namespace AuthManager.Application.Provisioning;

/// <summary>Subject/session identifiers come only from the authenticated transport boundary.</summary>
public interface IBrowserOrganisationCreation
{
    Task<OrganisationProvisioningResult> CreateAsync(Guid subjectId, Guid sessionId,
        OrganisationProvisioningCommand command, CancellationToken cancellationToken = default);
}

public static class BrowserOrganisationCreationCodes
{
    public const string InvalidRequest = "ORGANISATION_CREATION_REQUEST_INVALID";
    public const string Unauthenticated = "ORGANISATION_CREATION_UNAUTHENTICATED";
    public const string LimitReached = "ORGANISATION_OWNERSHIP_LIMIT_REACHED";
    public const string Conflict = "ORGANISATION_IDEMPOTENCY_CONFLICT";
    public const string InProgress = "ORGANISATION_CREATION_IN_PROGRESS";
    public const string ResultUncertain = "ORGANISATION_CREATION_RESULT_UNCERTAIN";
    public const string Unavailable = "ORGANISATION_CREATION_UNAVAILABLE";
}
