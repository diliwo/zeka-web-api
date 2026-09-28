using Zeka.Lifecycle.Contracts;
using AuthManager.Core.Lifecycle;

namespace AuthManager.Application.Lifecycle;

public enum ExportProgressStatus
{
    Progressed,
    Replay,
    AwaitingParticipants,
    Completed,
    Conflict,
    Rejected,
    Unavailable
}

public sealed record ExportProgressResult(
    ExportProgressStatus Status,
    Guid OperationId,
    long OperationRevision,
    string? PackageSha256 = null,
    string? PackageReference = null);

public interface ILifecycleExportStore
{
    Task<ExportProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken);
    Task<ExportProgressResult> AcceptFenceAsync(OrganisationExportFenceEnteredV1 receipt,
        CancellationToken cancellationToken);
    Task<ExportProgressResult> AcceptFragmentAsync(OrganisationExportFragmentReadyV1 fragment,
        CancellationToken cancellationToken);
    Task<ExportProgressResult> AcceptReleaseAsync(OrganisationExportFenceReleasedV1 release,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> RecoverableAsync(Guid organisationId, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<ExportProgressResult> ResumeAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generic lifecycle export coordinator. Participant routing and completeness are derived exclusively
/// from the immutable inventory pinned at admission; no service identity is branched here.
/// </summary>
public sealed class LifecycleExportCoordinator(ILifecycleExportStore store)
{
    public Task<ExportProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken = default) =>
        ValidateIdentity(operationId, organisationId)
            ? store.BeginAsync(operationId, organisationId, cancellationToken)
            : Task.FromResult(new ExportProgressResult(ExportProgressStatus.Rejected, operationId, 0));

    public Task<ExportProgressResult> ReceiveAsync(OrganisationExportFenceEnteredV1 receipt,
        CancellationToken cancellationToken = default) =>
        store.AcceptFenceAsync(receipt ?? throw new ArgumentNullException(nameof(receipt)), cancellationToken);

    public Task<ExportProgressResult> ReceiveAsync(OrganisationExportFragmentReadyV1 fragment,
        CancellationToken cancellationToken = default) =>
        store.AcceptFragmentAsync(fragment ?? throw new ArgumentNullException(nameof(fragment)), cancellationToken);

    public Task<ExportProgressResult> ReceiveAsync(OrganisationExportFenceReleasedV1 release,
        CancellationToken cancellationToken = default) =>
        store.AcceptReleaseAsync(release ?? throw new ArgumentNullException(nameof(release)), cancellationToken);

    public async Task<IReadOnlyList<ExportProgressResult>> RecoverAsync(Guid organisationId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (organisationId == Guid.Empty || now == default || now.Offset != TimeSpan.Zero) return [];
        var operations = await store.RecoverableAsync(organisationId, now, cancellationToken);
        var results = new List<ExportProgressResult>(operations.Count);
        foreach (var operation in operations.Order())
            results.Add(await store.ResumeAsync(operation, organisationId, cancellationToken));
        return results;
    }

    private static bool ValidateIdentity(Guid operationId, Guid organisationId) =>
        operationId != Guid.Empty && organisationId != Guid.Empty;
}

public static class AuthExportInventoryV1
{
    public const string ParticipantId = "auth-management";
    public const string Memberships = "memberships";
    public const string AuditEvents = "audit-events";
    public const string IntegrationEvents = "integration-events";

    public static readonly IReadOnlyList<string> Categories =
        Array.AsReadOnly([Memberships, AuditEvents, IntegrationEvents]);
}

public sealed record ExportPackageCategory(
    string ParticipantId,
    string Category,
    string Disposition,
    long RecordCount,
    string? SchemaVersion,
    string? ContentSha256,
    string? ArtifactReference,
    string? ReasonCode,
    string FragmentHash);

public sealed record ExportPackageInput(
    Guid OperationId,
    Guid OrganisationId,
    Guid RegistryRevision,
    string InventoryHash,
    string ExportInventoryHash,
    DateTimeOffset SnapshotAt,
    string FenceEvidenceHash,
    Guid RequestedBySubjectId,
    DateTimeOffset RequestedAt,
    DateTimeOffset CompletedAt,
    string CompletionStatus,
    IReadOnlyList<ExportPackageCategory> Categories,
    IReadOnlyDictionary<string, byte[]> Artifacts);

public sealed record ExportPackageOutput(
    byte[] Content,
    byte[] Manifest,
    string ManifestSha256,
    string PackageSha256);

public interface IExportPackageAssembler
{
    ExportPackageOutput Assemble(ExportPackageInput input);
}

public interface IExportPackageSink
{
    Task<string> StoreAsync(Guid operationId, string packageSha256, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

public interface IExportArtifactSource
{
    Task<ReadOnlyMemory<byte>> ReadAsync(string artifactReference, CancellationToken cancellationToken);
}

public interface IExportArtifactSink
{
    Task<string> StoreAsync(string artifactReference, string contentSha256,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
}

public interface IReviewedExportCategoryInventory
{
    IReadOnlyList<ReviewedExportCategoryRequirement> RequirementsFor(string participantId);
    LifecycleExportInventory Freeze(LifecycleRegistry registry);
}

public sealed record ReviewedExportCategoryRequirement(
    string Category,
    IReadOnlySet<ExportCategoryDispositionV1> AllowedDispositions);

/// <summary>Immutable LIFE-01 reviewed category inventory; production registry activation remains separate.</summary>
public sealed class ReviewedExportCategoryInventoryV1 : IReviewedExportCategoryInventory
{
    private static readonly IReadOnlySet<ExportCategoryDispositionV1> Materialized =
        new HashSet<ExportCategoryDispositionV1> { ExportCategoryDispositionV1.Included, ExportCategoryDispositionV1.Empty };
    private static readonly IReadOnlySet<ExportCategoryDispositionV1> Withheld =
        new HashSet<ExportCategoryDispositionV1> { ExportCategoryDispositionV1.Withheld };
    private static readonly IReadOnlySet<ExportCategoryDispositionV1> NotImplemented =
        new HashSet<ExportCategoryDispositionV1> { ExportCategoryDispositionV1.NotImplemented };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<ReviewedExportCategoryRequirement>> Inventory =
        new Dictionary<string, IReadOnlyList<ReviewedExportCategoryRequirement>>(StringComparer.Ordinal)
        {
            ["auth-management"] = Array.AsReadOnly([
                Required("audit-events", Withheld), Required("integration-events", Withheld), Required("memberships", Materialized)]),
            ["admin-area"] = MaterializedRequirements(["partner-contacts", "partner-document-metadata", "partner-emails",
                "partners", "staff-members", "teams"]),
            ["admin-area-documents"] = MaterializedRequirements(["partner-document-artifacts"]),
            ["client-management"] = Array.AsReadOnly([
                Required("audit-events", NotImplemented), Required("beneficiaries", Materialized),
                Required("beneficiary-assignments", Materialized), Required("case-histories", Materialized),
                Required("generated-report-artifacts", NotImplemented), Required("integration-events", NotImplemented),
                Required("notes", Withheld), Required("professional-history", Materialized),
                Required("school-history", Materialized), Required("structured-assessments", Materialized),
                Required("structured-reports", Materialized)])
        };

    public IReadOnlyList<ReviewedExportCategoryRequirement> RequirementsFor(string participantId) =>
        Inventory.TryGetValue(participantId, out var requirements) ? requirements
            : throw new InvalidOperationException("Participant is absent from the reviewed LIFE-01 category inventory.");

    public LifecycleExportInventory Freeze(LifecycleRegistry registry) => LifecycleExportInventory.Create(registry,
        registry.Inventory.Where(x => x.Capability.Family == LifecycleOperationFamily.Export
                && x.Capability.Key.EndsWith(".export-fragment", StringComparison.Ordinal))
            .SelectMany(binding => RequirementsFor(binding.ParticipantId).Select(requirement =>
                new LifecycleExportCategoryRequirement(binding.ParticipantId, requirement.Category,
                    requirement.AllowedDispositions.Select(x => x switch
                    {
                        ExportCategoryDispositionV1.Included => "included",
                        ExportCategoryDispositionV1.Empty => "empty",
                        ExportCategoryDispositionV1.NotImplemented => "not_implemented",
                        ExportCategoryDispositionV1.Withheld => "withheld",
                        _ => throw new InvalidOperationException("Unsupported reviewed disposition.")
                    }).ToArray()))));

    private static ReviewedExportCategoryRequirement Required(string category,
        IReadOnlySet<ExportCategoryDispositionV1> allowed) => new(category, allowed);
    private static IReadOnlyList<ReviewedExportCategoryRequirement> MaterializedRequirements(string[] categories) =>
        Array.AsReadOnly(categories.Order(StringComparer.Ordinal).Select(category => Required(category, Materialized)).ToArray());
}
