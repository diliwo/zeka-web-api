using System.Security.Cryptography;
using System.Text;

namespace Zeka.Lifecycle.Contracts;

public static class LifecycleContractV1
{
    public const int Version = 1;
}

public static class LifecycleContractTimeV1
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException("Lifecycle timestamps must be non-default UTC values.", nameof(value));

        return new DateTimeOffset(
            value.Ticks - value.Ticks % TicksPerMicrosecond,
            TimeSpan.Zero);
    }
}

public static class LifecycleMessageIdentityV1
{
    public static Guid ForPhase(Guid operationId, string participantId, string phase,
        long operationRevision)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("Operation identity is required.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(participantId) || participantId != participantId.Trim())
            throw new ArgumentException("Participant identity is required.", nameof(participantId));
        if (string.IsNullOrWhiteSpace(phase) || phase != phase.Trim())
            throw new ArgumentException("Lifecycle phase is required.", nameof(phase));
        if (operationRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(operationRevision));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"zeka-life02-v1\n{operationId:D}\n{participantId}\n{phase}\n{operationRevision}"));
        Span<byte> value = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(value);
        value[7] = (byte)((value[7] & 0x0f) | 0x50);
        value[8] = (byte)((value[8] & 0x3f) | 0x80);
        return new Guid(value);
    }
}

public static class OrganisationExportCapabilityV1
{
    public const string Fence = "organisation.export-fence";
    public const string Fragment = "organisation.export-fragment";
}

public static class OrganisationClosureCapabilityV1
{
    public const string Fence = "organisation.closure-fence";
}

public enum OrganisationClosureParticipantPhaseV1
{
    EnterFence = 1,
    ReleaseFence = 2
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
