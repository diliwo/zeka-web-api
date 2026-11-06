using System.Diagnostics;

namespace AdminAreaManagement.Application.Common.Authorization;

/// <summary>Safe operational signals, not a durable security-audit writer.</summary>
public static class TenantAccessDiagnostics
{
    public const string ResponseMalformed = "AUTH_RESPONSE_MALFORMED";
    public const string DependencyUnavailable = "AUTH_DEPENDENCY_UNAVAILABLE";
    public const string IntegrityMismatch = "AUTH_RESPONSE_INTEGRITY_MISMATCH";
    public const string PermissionCheckUnknown = "AUTH_PERMISSION_CHECK_UNKNOWN";
    public const string DiagnosticsName = "AdminAreaManagement.TenantAccess";
    private static readonly ActivitySource Signals = new(DiagnosticsName);

    public static string? KnownReason(string? reason) => reason switch
    {
        ResponseMalformed or DependencyUnavailable or IntegrityMismatch or PermissionCheckUnknown => reason,
        _ => null
    };

    public static bool ValidPermissions(IReadOnlyCollection<string>? codes)
    {
        if (codes is null) return false;
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in codes)
            if (!IsWellFormedPermission(code) || !unique.Add(code)) return false;
        return true;
    }

    // Ordinal identifiers. Unknown valid extras are not authority.
    public static bool IsWellFormedPermission(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var segmentStart = true;
        foreach (var c in code)
        {
            if (c == '.')
            {
                if (segmentStart) return false;
                segmentStart = true;
                continue;
            }
            var letter = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
            if (!letter && (segmentStart || c is not (>= '0' and <= '9'))) return false;
            segmentStart = false;
        }
        return !segmentStart;
    }

    public static string[] RecognizedPermissions(IReadOnlyCollection<string> codes) =>
        codes.Where(PermissionCatalogue.IsKnown).ToArray();

    public static void Integrity(string mismatchClass) => Signal(IntegrityMismatch, mismatchClass switch
    {
        "subject_mismatch" => "subject_mismatch",
        "organisation_mismatch" => "organisation_mismatch",
        _ => "contradictory_response"
    });

    public static void UnknownCheckedPermission() => Signal(PermissionCheckUnknown, null);

    private static void Signal(string code, string? mismatchClass) => Observe(() =>
    {
        using var activity = Signals.StartActivity("tenant_access.diagnostic");
        var tags = new ActivityTagsCollection
        {
            { "event_code", code }, { "service", "AdminAreaManagement" }, { "contract_version", 1 }
        };
        if (mismatchClass is not null) tags.Add("mismatch_class", mismatchClass);
        activity?.AddEvent(new ActivityEvent(code, DateTimeOffset.UtcNow, tags));
    });

    // Exporter/listener failure must never change the authorization decision.
    public static void Observe(Action observation)
    {
        try { observation(); }
        catch (Exception) { /* Operational delivery is best effort; authorization remains fail closed. */ }
    }
}
