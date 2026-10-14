using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Infrastructure.Persistence.Entities;

/// <summary>
/// Immutable, one-use approval receipt and attributed configuration audit in one row.
/// Its ApprovalId primary key is the replay fence across processes.
/// </summary>
public sealed class TimeZoneConfigurationAudit : ITenantOwnedEntity
{
    private TimeZoneConfigurationAudit() { }

    public Guid ApprovalId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ArtifactSha256 { get; private set; } = "";
    public string OwnerKeyId { get; private set; } = "";
    public string ExecutorSubject { get; private set; } = "";
    public string ReviewerSubject { get; private set; } = "";
    public string ReleaseEvidenceId { get; private set; } = "";
    public string ChangeReference { get; private set; } = "";
    public string? PreviousTimeZoneId { get; private set; }
    public string TimeZoneId { get; private set; } = "";
    public long PreviousVersion { get; private set; }
    public long NewVersion { get; private set; }
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = "";

    public static TimeZoneConfigurationAudit Create(
        Guid approvalId, Guid organisationId, string artifactSha256, string ownerKeyId,
        string executorSubject, string reviewerSubject, string releaseEvidenceId,
        string changeReference, string? previousTimeZoneId, string timeZoneId,
        long previousVersion, DateTimeOffset occurredAtUtc, string correlationId) => new()
    {
        ApprovalId = approvalId,
        OrganisationId = organisationId,
        ArtifactSha256 = artifactSha256,
        OwnerKeyId = ownerKeyId,
        ExecutorSubject = executorSubject,
        ReviewerSubject = reviewerSubject,
        ReleaseEvidenceId = releaseEvidenceId,
        ChangeReference = changeReference,
        PreviousTimeZoneId = previousTimeZoneId,
        TimeZoneId = timeZoneId,
        PreviousVersion = previousVersion,
        NewVersion = checked(previousVersion + 1),
        OccurredAtUtc = occurredAtUtc,
        CorrelationId = correlationId
    };
}
