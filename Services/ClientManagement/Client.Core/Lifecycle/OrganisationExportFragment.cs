using ClientManagement.Core.Common;

namespace ClientManagement.Core.Lifecycle;

public sealed class OrganisationExportFragment : TenantOwnedEntity
{
    private OrganisationExportFragment() { }

    public Guid OperationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string FenceToken { get; private set; } = string.Empty;
    public string FenceEvidenceHash { get; private set; } = string.Empty;
    public DateTimeOffset SnapshotAt { get; private set; }
    public string Disposition { get; private set; } = string.Empty;
    public long RecordCount { get; private set; }
    public long SoftDeletedRecordCount { get; private set; }
    public string? SchemaVersion { get; private set; }
    public string? ContentSha256 { get; private set; }
    public string? ArtifactReference { get; private set; }
    public string? ReasonCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static OrganisationExportFragment Create(Guid operationId, Guid organisationId, long operationRevision,
        string participantId, string category, string fenceToken, string fenceEvidenceHash,
        DateTimeOffset snapshotAt, string disposition, long recordCount, long softDeletedRecordCount,
        string? schemaVersion, string? contentSha256, string? artifactReference, string? reasonCode,
        DateTimeOffset createdAt)
    {
        if (operationId == Guid.Empty || organisationId == Guid.Empty || operationRevision <= 0
            || string.IsNullOrWhiteSpace(participantId) || string.IsNullOrWhiteSpace(category)
            || string.IsNullOrWhiteSpace(fenceToken) || string.IsNullOrWhiteSpace(fenceEvidenceHash)
            || snapshotAt == default || snapshotAt.Offset != TimeSpan.Zero || recordCount < 0
            || softDeletedRecordCount < 0 || softDeletedRecordCount > recordCount
            || createdAt == default || createdAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Export fragment identity is invalid.");
        var fragment = new OrganisationExportFragment
        {
            OperationId = operationId,
            OperationRevision = operationRevision,
            ParticipantId = participantId,
            Category = category,
            FenceToken = fenceToken,
            FenceEvidenceHash = fenceEvidenceHash,
            SnapshotAt = snapshotAt,
            Disposition = disposition,
            RecordCount = recordCount,
            SoftDeletedRecordCount = softDeletedRecordCount,
            SchemaVersion = schemaVersion,
            ContentSha256 = contentSha256,
            ArtifactReference = artifactReference,
            ReasonCode = reasonCode,
            CreatedAt = createdAt
        };
        fragment.AssignToOrganisation(organisationId);
        return fragment;
    }
}
