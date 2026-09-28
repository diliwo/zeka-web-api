using Zeka.Lifecycle.Contracts;

namespace AdminAreaManagement.Application.Exports;

public static class AdminAreaExportContractV1
{
    public const string ParticipantId = "admin-area";
    public const string DocumentParticipantId = "admin-area-documents";
    public const string FenceCapability = "export-fence";
    public const string FragmentCapability = "admin-area.export-fragment";
    public const string DocumentFragmentCapability = "admin-area-documents.export-fragment";

    public static readonly IReadOnlyList<string> StructuredCategories =
    [
        "partner-contacts",
        "partner-document-metadata",
        "partner-emails",
        "partners",
        "staff-members",
        "teams"
    ];

    public static readonly IReadOnlyList<string> DocumentCategories = ["partner-document-artifacts"];
}

public interface IAdminAreaExportParticipant
{
    Task<OrganisationExportFenceEnteredV1> EnterFenceAsync(
        EnterOrganisationExportFenceV1 command, CancellationToken cancellationToken = default);

    Task<OrganisationExportFragmentReadyV1> StageFragmentAsync(
        StageOrganisationExportV1 command, CancellationToken cancellationToken = default);

    Task<OrganisationExportFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationExportFenceV1 command, CancellationToken cancellationToken = default);
}

public sealed record ExportArtifactWrite(
    string SetName,
    string RelativeName,
    ReadOnlyMemory<byte> Content);

public sealed record ExportArtifactReceipt(
    string ArtifactReference,
    string ContentSha256,
    long Length);

public interface IAdminAreaExportArtifactStore
{
    Task<ExportArtifactReceipt> WriteVerifiedAsync(
        Guid organisationId,
        Guid operationId,
        ExportArtifactWrite artifact,
        CancellationToken cancellationToken = default);

    Task VerifyExactAsync(
        Guid organisationId,
        Guid operationId,
        string setName,
        IReadOnlyCollection<ExportArtifactReceipt> expected,
        CancellationToken cancellationToken = default);
}
