using System.Text.Json.Serialization;
using System.Text.Json;
using Npgsql;
using System.Text.RegularExpressions;

namespace Zeka.ToolingDiagnostics;

internal interface ISafePostgresDiagnosticFailure { SafePostgresDiagnostic? PostgresDiagnostic { get; } }

// Only this four-field projection may leave PostgreSQL owner-tool error boundaries.
// MessageText/Hint are explicitly owner-approved server fields; they are not Exception.Message.
internal sealed record SafePostgresDiagnostic(
    [property: JsonPropertyName("sqlState")] string SqlState,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("hint")] string? Hint,
    [property: JsonPropertyName("position")] int Position)
{
    internal static SafePostgresDiagnostic? From(Exception? error, Func<string, string>? knownRedactor = null)
    {
        string Protect(string value) => SafePostgresText.Protect(knownRedactor is null ? value : knownRedactor(value));
        for (var depth = 0; depth < 16 && error is not null; depth++, error = error.InnerException)
        {
            if (error is ISafePostgresDiagnosticFailure { PostgresDiagnostic: { } captured })
                return captured with { Message = Protect(captured.Message), Hint = captured.Hint is null ? null : Protect(captured.Hint) };
            if (error is PostgresException postgres)
                return new(postgres.SqlState, Protect(postgres.MessageText),
                    postgres.Hint is null ? null : Protect(postgres.Hint), postgres.Position);
        }
        return null;
    }

    internal static SafePostgresDiagnostic Parse(JsonElement value)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
            var fields = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Length != 4 || fields.Distinct(StringComparer.Ordinal).Count() != 4 ||
                fields.Any(f => f is not ("sqlState" or "message" or "hint" or "position")))
                throw new InvalidOperationException();
            var state = value.GetProperty("sqlState");
            var message = value.GetProperty("message");
            var hint = value.GetProperty("hint");
            var position = value.GetProperty("position");
            if (state.ValueKind != JsonValueKind.String || message.ValueKind != JsonValueKind.String ||
                hint.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                position.ValueKind != JsonValueKind.Number || !position.TryGetInt32(out var number) || number < 0)
                throw new InvalidOperationException();
            var code = state.GetString();
            var text = message.GetString();
            if (code is null || code.Length != 5 || code.Any(c => !(c is >= '0' and <= '9' or >= 'A' and <= 'Z')) || text is null)
                throw new InvalidOperationException();
            return new(code, SafePostgresText.Protect(text),
                hint.ValueKind == JsonValueKind.Null ? null : SafePostgresText.Protect(hint.GetString()!), number);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or JsonException)
        {
            throw new InvalidOperationException("POSTGRES_DIAGNOSTIC_REJECTED");
        }
    }

    internal static string WithoutErrorDetail(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString) { IncludeErrorDetail = false }.ConnectionString;
}

// Capture before an application boundary deliberately drops provider InnerException.
// One invocation owns one collector; later cleanup failures cannot replace its first PostgreSQL failure.
internal sealed class SafePostgresDiagnosticCapture
{
    private SafePostgresDiagnostic? first;
    private readonly List<string> secrets = [];
    internal void RegisterSecret(string? value) { if (!string.IsNullOrEmpty(value)) secrets.Add(value); }
    internal void RegisterConnection(string value)
    {
        RegisterSecret(value);
        RegisterSecret(new NpgsqlConnectionStringBuilder(value).Password);
    }
    private string Redact(string value)
    {
        foreach (var secret in secrets.OrderByDescending(secret => secret.Length)) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
    internal SafePostgresDiagnostic? Diagnostic => Volatile.Read(ref first);
    internal void Capture(Exception error)
    {
        if (SafePostgresDiagnostic.From(error, Redact) is { } diagnostic)
            Interlocked.CompareExchange(ref first, diagnostic, null);
    }
}

// Applied only to approved text fields, never SQLSTATE, position or unquoted operator/type names.
internal static class SafePostgresText
{
    private static readonly Regex[] Rules = new[] {
        "(?i)(?:host|server|data source)\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^;\\r\\n]+)(?:\\s*;\\s*[a-z_ ]+\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^;\\r\\n]+))*",
        "(?i)\\b(?:password|passwd|pwd|token|secret|access_token|refresh_token|client_secret|api_key|bearer_token)\\b\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s;,\\r\\n]+)",
        "(?i)\\bBearer\\s+[^\\s;,\\r\\n]+",
        "(?:gh[pousr]_|github_pat_|sqa_|squ_|sqp_|sk_live_|sk_test_)[A-Za-z0-9_=-]{8,}",
        "[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}",
        "(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}",
        "(?i)[a-z][a-z0-9+.-]*://[^\\s/@:]+:[^\\s/@]+@[^\\s]+",
        "[A-Za-z]:[\\\\/][^;\\r\\n]+",
        "\\\\\\\\[^;\\r\\n]+",
        "/(?:[^/\\s;,]+/)+[^;\\r\\n]*",
        "'(?:''|[^'])*(?:'|$)",
        "\"(?:\"\"|[^\"])*(?:\"|$)"
    }.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100))).ToArray();
    internal static string Protect(string value)
    {
        try { foreach (var rule in Rules) value = rule.Replace(value, "[REDACTED]"); return value; }
        catch (RegexMatchTimeoutException) { return "[POSTGRES_TEXT_WITHHELD]"; }
    }
}
