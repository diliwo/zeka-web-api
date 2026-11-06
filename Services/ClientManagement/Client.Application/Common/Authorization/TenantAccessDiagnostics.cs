using System.Diagnostics;

namespace ClientManagement.Application.Common.Authorization;

// Operational telemetry only: this is not a durable security-audit writer.
public static class TenantAccessDiagnostics
{
    public const string ResponseMalformed = "AUTH_RESPONSE_MALFORMED";
    public const string DependencyUnavailable = "AUTH_DEPENDENCY_UNAVAILABLE";
    public const string IntegrityMismatch = "AUTH_RESPONSE_INTEGRITY_MISMATCH";
    public const string PermissionCheckUnknown = "AUTH_PERMISSION_CHECK_UNKNOWN";
    private static readonly ActivitySource Events = new("ClientManagement.TenantAccess");

    public static bool ValidPermissions(IReadOnlyCollection<string>? codes) => codes is not null
        && codes.All(IsWellFormedPermission) && codes.Distinct(StringComparer.Ordinal).Count() == codes.Count;

    public static string[] RecognizedPermissions(IReadOnlyCollection<string> codes) =>
        codes.Where(PermissionCatalogue.IsKnown).ToArray();

    private static bool IsWellFormedPermission(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var segmentStart = true;
        foreach (var character in code)
        {
            if (character == '.')
            {
                if (segmentStart) return false;
                segmentStart = true;
            }
            else
            {
                if (!(char.IsAsciiLetter(character) || !segmentStart && char.IsAsciiDigit(character))) return false;
                segmentStart = false;
            }
        }
        return !segmentStart;
    }

    public static string? KnownReason(string? reason) => reason is ResponseMalformed or DependencyUnavailable
        or IntegrityMismatch or PermissionCheckUnknown ? reason : null;

    public static void Integrity(string mismatchClass) => Emit(IntegrityMismatch,
        mismatchClass is "subject_mismatch" or "organisation_mismatch" ? mismatchClass : "contradictory_response");

    public static void UnknownCheckedPermission() => Emit(PermissionCheckUnknown, null);

    private static void Emit(string code, string? mismatchClass) => Observe(() =>
    {
        using var activity = Events.StartActivity("tenant_access.diagnostic", ActivityKind.Internal);
        var tags = new ActivityTagsCollection
        {
            ["event_code"] = code,
            ["service"] = "ClientManagement",
            ["contract_version"] = 1
        };
        if (mismatchClass is not null) tags.Add("mismatch_class", mismatchClass);
        activity?.AddEvent(new ActivityEvent(code, DateTimeOffset.UtcNow, tags));
    });

    public static void Observe(Action observation)
    {
        try { observation(); }
        catch (Exception)
        {
            // An operational exporter/listener failure must not change the authorization decision.
        }
    }
}