using Xunit;
using System.Text.Json;
using Npgsql;
using Zeka.ToolingDiagnostics;

namespace Zeka.AuthOwnerProvision.Tests;

public sealed class SafePostgresDiagnosticTests
{
    [Fact]
    public void Unique_violation_detail_and_other_nonapproved_fields_never_enter_projection()
    {
        var canary = Guid.NewGuid().ToString("N");
        var error = new PostgresException("Synthetic duplicate key rejection.", "ERROR", "ERROR", "23505",
            detail: canary, hint: "Use a distinct synthetic identifier.", position: 17,
            internalPosition: 23, internalQuery: canary, where: canary,
            schemaName: canary, tableName: canary, columnName: canary, dataTypeName: canary,
            constraintName: canary, file: canary, line: canary, routine: canary);
        var projected = SafePostgresDiagnostic.From(new InvalidOperationException("Withheld wrapper.", error));
        Assert.NotNull(projected);
        Assert.Equal("23505", projected!.SqlState);
        Assert.Equal(error.MessageText, projected.Message);
        Assert.Equal(error.Hint, projected.Hint);
        Assert.Equal(17, projected.Position);
        var json = JsonSerializer.Serialize(projected);
        Assert.True(!json.Contains(canary, StringComparison.Ordinal));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "sqlState", "message", "hint", "position" },
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Unexpected_exception_text_is_never_promoted_to_postgres_message()
    {
        var canary = Guid.NewGuid().ToString("N");
        Assert.Null(SafePostgresDiagnostic.From(new InvalidOperationException(canary)));
        Assert.Null(SafePostgresDiagnostic.From(null));
    }

    [Fact]
    public void Collector_preserves_first_postgres_error_across_later_cleanup_errors()
    {
        var capture = new SafePostgresDiagnosticCapture();
        capture.Capture(new PostgresException("First synthetic failure.", "ERROR", "ERROR", "23505", position: 9));
        capture.Capture(new PostgresException("Later cleanup failure.", "ERROR", "ERROR", "42501"));
        Assert.Equal("23505", capture.Diagnostic!.SqlState);
        Assert.Equal(9, capture.Diagnostic.Position);
    }

    [Fact]
    public void Registered_credential_values_are_redacted_from_approved_message_and_hint_fields()
    {
        var canary = Guid.NewGuid().ToString("N");
        var capture = new SafePostgresDiagnosticCapture();
        capture.RegisterSecret(canary);
        capture.Capture(new PostgresException("Synthetic value " + canary, "ERROR", "ERROR", "23505", hint: canary));
        var json = JsonSerializer.Serialize(capture.Diagnostic);
        Assert.True(!json.Contains(canary, StringComparison.Ordinal));
        Assert.True(json.Contains("[REDACTED]", StringComparison.Ordinal));
    }

    [Fact]
    public void Connection_policy_forces_error_detail_off_without_printing_credential()
    {
        var value = Guid.NewGuid().ToString("N");
        var input = new NpgsqlConnectionStringBuilder { Host = "fixture.invalid", Database = "fixture",
            Username = "fixture", Password = value, IncludeErrorDetail = true };
        var result = new NpgsqlConnectionStringBuilder(SafePostgresDiagnostic.WithoutErrorDetail(input.ConnectionString));
        Assert.False(result.IncludeErrorDetail);
        Assert.True(string.Equals(value, result.Password, StringComparison.Ordinal));
    }
    public static IEnumerable<object[]> InvalidTypedFields()
    {
        foreach (var value in new[] { "null", "[]", "true", "17", "\"text\"" }) yield return [value];
        foreach (var (field, bad) in new (string, object?)[] {
            ("sqlState", null), ("sqlState", 23505), ("sqlState", "abcde"), ("sqlState", "1234"), ("sqlState", "123456"), ("sqlState", "42-83"),
            ("message", null), ("message", 17), ("message", false), ("message", new[] { "text" }),
            ("hint", 17), ("hint", false), ("hint", new[] { "text" }),
            ("position", null), ("position", -1), ("position", "17"), ("position", true), ("position", 1.25), ("position", 2147483648L) })
        {
            var fields = new Dictionary<string, object?> { ["sqlState"] = "42883", ["message"] = "Synthetic message.", ["hint"] = null, ["position"] = 17 };
            fields[field] = bad;
            yield return [JsonSerializer.Serialize(fields)];
        }
        foreach (var field in new[] { "sqlState", "message", "hint", "position" })
        {
            var fields = new Dictionary<string, object?> { ["sqlState"] = "42883", ["message"] = "Synthetic message.", ["hint"] = null, ["position"] = 17 };
            fields.Remove(field);
            yield return [JsonSerializer.Serialize(fields)];
        }
        yield return ["{\"sqlState\":\"42883\",\"message\":\"Synthetic.\",\"hint\":null,\"position\":1.0}"];
        yield return ["{\"sqlState\":\"42883\",\"message\":\"one\",\"message\":\"two\",\"position\":0}"];
        yield return ["{\"sqlState\":\"42883\",\"message\":\"Synthetic.\",\"hint\":null,\"position\":0,\"detail\":\"excluded\"}"];
    }

    [Theory]
    [MemberData(nameof(InvalidTypedFields))]
    public void Typed_parser_rejects_malformed_fields_with_uniform_safe_code(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = Assert.Throws<InvalidOperationException>(() => SafePostgresDiagnostic.Parse(document.RootElement));
        Assert.Equal("POSTGRES_DIAGNOSTIC_REJECTED", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Complete_known_secrets_are_removed_before_delimiter_rules_without_fragment_leakage()
    {
        foreach (var separator in new[] { "'", "\"", "/private/", "\\", Environment.NewLine, ";" })
        {
            var prefix = Guid.NewGuid().ToString("N");
            var suffix = Guid.NewGuid().ToString("N");
            var secret = prefix + separator + suffix;
            var capture = new SafePostgresDiagnosticCapture();
            capture.RegisterSecret(prefix); // Overlap must not cause a partial full-secret match.
            capture.RegisterSecret(secret);
            capture.Capture(new PostgresException("Synthetic " + secret, "ERROR", "ERROR", "23505", hint: secret));
            var output = JsonSerializer.Serialize(capture.Diagnostic);
            Assert.True(!output.Contains(prefix, StringComparison.Ordinal));
            Assert.True(!output.Contains(suffix, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Typed_sqlstate_terminal_newline_is_rejected()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { sqlState = "42883" + (char)10, message = "Synthetic.", hint = (string?)null, position = 0 }));
        var error = Assert.Throws<InvalidOperationException>(() => SafePostgresDiagnostic.Parse(document.RootElement));
        Assert.Equal("POSTGRES_DIAGNOSTIC_REJECTED", error.Message);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(17, "Add explicit casts.")]
    public void Typed_parser_preserves_valid_fields(int position, string? hint)
    {
        var expected = new SafePostgresDiagnostic("42883", "Synthetic message.", hint, position);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(expected));
        Assert.Equal(expected, SafePostgresDiagnostic.Parse(document.RootElement));
    }

    [Fact]
    public void Approved_text_fields_redact_sensitive_shapes_including_multiline_literals()
    {
        foreach (var kind in new[] { "credential", "bearer", "pat", "connection", "email", "windows", "unix", "unc", "literal", "identifier", "escaped-literal", "multiline-literal", "multiline-identifier" })
        {
            var canary = Guid.NewGuid().ToString("N");
            var payload = kind switch {
                "credential" => "Password=" + canary, "bearer" => "Bearer " + canary, "pat" => "ghp_" + canary,
                "connection" => "Host=fixture;Database=fixture;Username=fixture;Password=" + canary,
                "email" => "synthetic." + canary + "@example.invalid", "windows" => @"C:\private\" + canary,
                "unix" => "/private/" + canary, "unc" => @"\\private\" + canary,
                "literal" => "'" + canary + "'", "identifier" => "\"" + canary + "\"",
                "escaped-literal" => "'before''" + canary + "'", "multiline-literal" => "'first" + Environment.NewLine + canary + "'",
                _ => "\"first" + Environment.NewLine + canary + "\""
            };
            var error = new PostgresException("Synthetic " + payload, "ERROR", "ERROR", "42883", hint: payload, position: 17);
            var projected = SafePostgresDiagnostic.From(error)!;
            var json = JsonSerializer.Serialize(projected);
            Assert.True(!json.Contains(canary, StringComparison.Ordinal), "Sensitive shape: " + kind);
            Assert.Equal("42883", projected.SqlState);
            Assert.Equal(17, projected.Position);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(new SafePostgresDiagnostic("42883", payload, payload, 17)));
            Assert.True(!JsonSerializer.Serialize(SafePostgresDiagnostic.Parse(document.RootElement)).Contains(canary, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Operator_names_types_and_useful_multiline_hint_remain_exact()
    {
        const string message = "operator does not exist: name[] = text[]";
        var hint = "No operator matches the given name and argument types." + Environment.NewLine + "You might need to add explicit type casts.";
        var result = SafePostgresDiagnostic.From(new PostgresException(message, "ERROR", "ERROR", "42883", hint: hint, position: 17))!;
        Assert.Equal(message, result.Message);
        Assert.Equal(hint, result.Hint);
    }

}
