namespace Zeka.Lifecycle.Contracts;

public static class LifecycleContractV1
{
    public const int Version = 1;
}

public static class OrganisationExportCapabilityV1
{
    public const string Fence = "organisation.export-fence";
    public const string Fragment = "organisation.export-fragment";
}

public enum OrganisationExportParticipantPhaseV1
{
    EnterFence = 1,
    StageFragment = 2,
    ReleaseFence = 3
}

public enum ExportCategoryDispositionV1
{
    Included = 1,
    Empty = 2,
    NotImplemented = 3,
    Withheld = 4
}

public static class ExportCategoryDispositionV1Extensions
{
    public static string ToContractValue(this ExportCategoryDispositionV1 disposition) => disposition switch
    {
        ExportCategoryDispositionV1.Included => "included",
        ExportCategoryDispositionV1.Empty => "empty",
        ExportCategoryDispositionV1.NotImplemented => "not_implemented",
        ExportCategoryDispositionV1.Withheld => "withheld",
        _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown category disposition.")
    };
}
