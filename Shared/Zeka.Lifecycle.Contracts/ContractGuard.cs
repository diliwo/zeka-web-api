using System.Text.RegularExpressions;

namespace Zeka.Lifecycle.Contracts;

internal static partial class ContractGuard
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,499}$", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueReferencePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    public static Guid Required(Guid value, string name)
    {
        if (value == Guid.Empty)
            throw new ArgumentException($"{name} must not be empty.", name);
        return value;
    }

    public static long Positive(long value, string name)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, value, $"{name} must be positive.");
        return value;
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new ArgumentException($"{name} must be a non-default UTC timestamp.", name);
        return value;
    }

    public static string StableKey(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!StableKeyPattern().IsMatch(value))
            throw new ArgumentException($"{name} must be a stable ASCII key of at most 200 characters.", name);
        return value;
    }

    public static string OpaqueReference(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!OpaqueReferencePattern().IsMatch(value) || value.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"{name} must be a safe opaque reference without query, fragment, credentials or traversal.", name);
        return value;
    }

    public static string Sha256(string value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (!Sha256Pattern().IsMatch(value))
            throw new ArgumentException($"{name} must be a lowercase SHA-256 value.", name);
        return value;
    }

    public static int Version(int value, string name)
    {
        if (value != LifecycleContractV1.Version)
            throw new ArgumentOutOfRangeException(name, value, "Only lifecycle contract version 1 is supported.");
        return value;
    }
}
