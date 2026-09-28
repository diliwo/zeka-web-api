namespace Zeka.Lifecycle.Contracts;

public sealed record ExportCategoryFragmentV1
{
    public ExportCategoryFragmentV1(
        string category,
        ExportCategoryDispositionV1 disposition,
        long recordCount,
        string? schemaVersion,
        string? contentSha256,
        string? artifactReference,
        string? reasonCode)
    {
        Category = ContractGuard.StableKey(category, nameof(category));
        if (!Enum.IsDefined(disposition))
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown category disposition.");
        if (recordCount < 0)
            throw new ArgumentOutOfRangeException(nameof(recordCount), recordCount, "Record count cannot be negative.");

        Disposition = disposition;
        RecordCount = recordCount;
        if (disposition is ExportCategoryDispositionV1.Included or ExportCategoryDispositionV1.Empty)
        {
            if (disposition == ExportCategoryDispositionV1.Included && recordCount == 0)
                throw new ArgumentException("Included categories must contain at least one record.", nameof(recordCount));
            if (disposition == ExportCategoryDispositionV1.Empty && recordCount != 0)
                throw new ArgumentException("Empty categories must have a zero record count.", nameof(recordCount));
            SchemaVersion = ContractGuard.StableKey(schemaVersion!, nameof(schemaVersion));
            ContentSha256 = ContractGuard.Sha256(contentSha256!, nameof(contentSha256));
            ArtifactReference = ContractGuard.OpaqueReference(artifactReference!, nameof(artifactReference));
            if (reasonCode is not null)
                throw new ArgumentException("Materialized categories cannot carry an absence reason.", nameof(reasonCode));
        }
        else
        {
            if (recordCount != 0)
                throw new ArgumentException("Absent categories must have a zero record count.", nameof(recordCount));
            if (schemaVersion is not null || contentSha256 is not null || artifactReference is not null)
                throw new ArgumentException("Absent categories cannot claim schema, hash or artifact materialization.");
            ReasonCode = ContractGuard.StableKey(reasonCode!, nameof(reasonCode));
        }
    }

    public string Category { get; }
    public ExportCategoryDispositionV1 Disposition { get; }
    public string DispositionCode => Disposition.ToContractValue();
    public long RecordCount { get; }
    public string? SchemaVersion { get; }
    public string? ContentSha256 { get; }
    public string? ArtifactReference { get; }
    public string? ReasonCode { get; }
}

public sealed class OrganisationExportFragmentReadyV1
{
    public OrganisationExportFragmentReadyV1(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset snapshotAt,
        string fenceToken,
        IEnumerable<ExportCategoryFragmentV1> categories)
    {
        Header = header ?? throw new ArgumentNullException(nameof(header));
        SnapshotAt = ContractGuard.Utc(snapshotAt, nameof(snapshotAt));
        FenceToken = ContractGuard.StableKey(fenceToken, nameof(fenceToken));
        ArgumentNullException.ThrowIfNull(categories);
        var ordered = categories.OrderBy(x => x.Category, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
            throw new ArgumentException("A fragment must account for at least one frozen category.", nameof(categories));
        if (ordered.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("A fragment must account for each category exactly once.", nameof(categories));
        Categories = Array.AsReadOnly(ordered);
        FragmentHash = ComputeFragmentHash();
    }

    public LifecycleMessageHeaderV1 Header { get; }
    public DateTimeOffset SnapshotAt { get; }
    public string FenceToken { get; }
    public IReadOnlyList<ExportCategoryFragmentV1> Categories { get; }
    public string FragmentHash { get; }

    private string ComputeFragmentHash()
    {
        var hash = CanonicalLifecycleHash.ForHeader("zeka-lifecycle-export-fragment-v1", Header)
            .Add(SnapshotAt).Add(FenceToken).Add(Categories.Count);
        foreach (var category in Categories)
            hash.Add(category.Category).Add(category.DispositionCode).Add(category.RecordCount)
                .Add(category.SchemaVersion ?? "").Add(category.ContentSha256 ?? "")
                .Add(category.ArtifactReference ?? "").Add(category.ReasonCode ?? "");
        return hash.Finish();
    }
}
