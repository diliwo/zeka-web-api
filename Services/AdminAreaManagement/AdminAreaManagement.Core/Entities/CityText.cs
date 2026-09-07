using System.Text;

namespace AdminAreaManagement.Core.Entities;

/// <summary>Text policy for shared City reference data; display text is not rewritten.</summary>
public static class CityText
{
    public const int MaximumLength = 100;

    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Contains('\0')) return false;
        try
        {
            normalized = value.Normalize(NormalizationForm.FormC).Trim();
            return true;
        }
        catch (ArgumentException)
        {
            return false; // Ill-formed UTF-16 cannot be represented in PostgreSQL UTF-8 text.
        }
    }

    public static bool IsValid(string? value) =>
        TryNormalize(value, out var normalized) &&
        normalized.Length > 0 && ScalarLength(normalized) <= MaximumLength;

    public static int ScalarLength(string normalized)
    {
        var count = 0;
        foreach (var rune in normalized.EnumerateRunes()) count++;
        return count;
    }

    public static string Validate(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentNullException(parameterName);
        if (!IsValid(value))
            throw new ArgumentException(
                $"City text must contain 1 to {MaximumLength} Unicode scalar values after NFC normalization and outer trimming.",
                parameterName);
        return value;
    }
}
