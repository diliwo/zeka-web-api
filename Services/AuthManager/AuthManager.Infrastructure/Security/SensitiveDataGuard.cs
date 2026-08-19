using System.Text.Json;

namespace AuthManager.Infrastructure.Security;

internal static class SensitiveDataGuard
{
    private static readonly string[] ForbiddenNameFragments =
    [
        "password", "passwd", "secret", "token", "credential", "authorization", "cookie", "api_key", "apikey"
    ];

    public static void ValidateJson(string json, string parameterName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("A valid JSON document is required.", parameterName, exception);
        }

        using (document)
            ValidateElement(document.RootElement, parameterName);
    }

    public static void ValidateMetadata(IReadOnlyDictionary<string, string>? metadata, string parameterName)
    {
        if (metadata is null)
            return;

        foreach (var key in metadata.Keys)
            ValidateName(key, parameterName);
    }

    private static void ValidateElement(JsonElement element, string parameterName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                ValidateName(property.Name, parameterName);
                ValidateElement(property.Value, parameterName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateElement(item, parameterName);
        }
    }

    private static void ValidateName(string name, string parameterName)
    {
        if (ForbiddenNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Field '{name}' is not permitted because persisted operational data must not contain credentials.", parameterName);
    }
}
