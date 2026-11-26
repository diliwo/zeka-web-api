extern alias DbMigrator;
using System.Reflection;
using System.Text.Json;
using Npgsql;
using Xunit;
using SafePostgresDiagnosticCapture = DbMigrator::Zeka.ToolingDiagnostics.SafePostgresDiagnosticCapture;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-17")]
public sealed class MigrationDiagnosticBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manifest_boundary_captures_direct_and_reflection_wrapped_provider_errors(bool wrapped)
    {
        var native = ProviderError();
        Exception thrown = wrapped ? new TargetInvocationException(native) : native;
        var capture = new SafePostgresDiagnosticCapture();

        var observed = await Assert.ThrowsAnyAsync<Exception>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(thrown), capture));

        Assert.Same(thrown, observed);
        Assert.Equal("42883", capture.Diagnostic!.SqlState);
        Assert.Equal("operator does not exist: name[] = text[]", capture.Diagnostic.Message);
        Assert.Equal("Add explicit casts.", capture.Diagnostic.Hint);
        Assert.Equal(17, capture.Diagnostic.Position);
    }

    [Fact]
    public async Task Original_diagnostic_is_available_before_recovery_and_cannot_be_replaced_by_recovery_failure()
    {
        var original = ProviderError();
        var recovery = new PostgresException("Recovery failed.", "ERROR", "ERROR", "42501", position: 29);
        var capture = new SafePostgresDiagnosticCapture();
        var recoveryEntered = false;

        try
        {
            await MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(original), capture);
            Assert.True(false, "The boundary must rethrow the original failure.");
        }
        catch (PostgresException observed)
        {
            Assert.Same(original, observed);
            recoveryEntered = true;
            Assert.Equal("42883", capture.Diagnostic!.SqlState);
            capture.Capture(recovery);
        }

        Assert.True(recoveryEntered);
        Assert.Equal("42883", capture.Diagnostic!.SqlState);
        Assert.Equal(17, capture.Diagnostic.Position);
    }

    [Fact]
    public async Task Earlier_provider_failure_wins_over_later_manifest_failure()
    {
        var capture = new SafePostgresDiagnosticCapture();
        capture.Capture(new PostgresException("First provider failure.", "ERROR", "ERROR", "23505", position: 7));
        var later = ProviderError();

        var observed = await Assert.ThrowsAsync<PostgresException>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(later), capture));

        Assert.Same(later, observed);
        Assert.Equal("23505", capture.Diagnostic!.SqlState);
        Assert.Equal("First provider failure.", capture.Diagnostic.Message);
        Assert.Equal(7, capture.Diagnostic.Position);
    }

    [Fact]
    public async Task Manifest_projection_redacts_known_sensitive_values_and_never_exports_detail_or_query()
    {
        var canary = Guid.NewGuid().ToString("N");
        var omitted = Guid.NewGuid().ToString("N");
        var capture = new SafePostgresDiagnosticCapture();
        capture.RegisterSecret(canary);
        var native = new PostgresException("Synthetic rejected value " + canary, "ERROR", "ERROR", "42883",
            detail: omitted, hint: "Synthetic hint " + canary, position: 17,
            internalQuery: omitted, where: omitted, schemaName: omitted, tableName: omitted,
            columnName: omitted, dataTypeName: omitted, constraintName: omitted);

        await Assert.ThrowsAsync<PostgresException>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(native), capture));

        var json = JsonSerializer.Serialize(capture.Diagnostic);
        Assert.False(json.Contains(canary, StringComparison.Ordinal));
        Assert.False(json.Contains(omitted, StringComparison.Ordinal));
        Assert.Contains("[REDACTED]", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "sqlState", "message", "hint", "position" },
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("42883", capture.Diagnostic!.SqlState);
        Assert.Equal(17, capture.Diagnostic.Position);
    }

    [Fact]
    public async Task Synchronous_provider_failure_is_captured_before_rethrow()
    {
        var original = ProviderError();
        var capture = new SafePostgresDiagnosticCapture();
        var observed = await Assert.ThrowsAsync<PostgresException>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => throw original, capture));
        Assert.Same(original, observed);
        Assert.Equal("42883", capture.Diagnostic!.SqlState);
    }

    [Fact]
    public async Task Nonprovider_failure_preserves_original_exception_without_promoting_its_text()
    {
        var original = new InvalidOperationException(Guid.NewGuid().ToString("N"));
        var capture = new SafePostgresDiagnosticCapture();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(original), capture));
        Assert.Same(original, observed);
        Assert.Null(capture.Diagnostic);
    }

    [Fact]
    public async Task Cancellation_preserves_original_exception_and_does_not_invent_a_provider_diagnostic()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var original = new OperationCanceledException(source.Token);
        var capture = new SafePostgresDiagnosticCapture();
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            MigrationEngine.CapturePostgresFailureAsync(() => Task.FromException(original), capture));
        Assert.Same(original, observed);
        Assert.Equal(source.Token, observed.CancellationToken);
        Assert.Null(capture.Diagnostic);
    }

    [Fact]
    public async Task Successful_operation_is_invoked_once_and_leaves_no_failure_diagnostic()
    {
        var calls = 0;
        var capture = new SafePostgresDiagnosticCapture();
        await MigrationEngine.CapturePostgresFailureAsync(() => { calls++; return Task.CompletedTask; }, capture);
        Assert.Equal(1, calls);
        Assert.Null(capture.Diagnostic);
    }

    private static PostgresException ProviderError() =>
        new("operator does not exist: name[] = text[]", "ERROR", "ERROR", "42883",
            hint: "Add explicit casts.", position: 17);
}