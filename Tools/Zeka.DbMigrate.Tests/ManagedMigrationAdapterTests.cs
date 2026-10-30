using System.Text.Json;
using Zeka.LocalStack.DatabaseSetup;
using Xunit;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-17")]
[Trait("Evidence", "LocalStack")]
public sealed class ManagedMigrationAdapterTests
{
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Missing_terminal_record_preserves_safe_native_receipt_and_real_exit_first()
    {
        var context = Context();
        var export = NewRoot();
        var output = await ManagedMigrationAdapter.ObserveAsync(new StringReader(""));
        var error = await ManagedMigrationAdapter.ObserveAsync(new StringReader("EVIDENCE_PUBLISH_REJECTED\n"));
        var receipt = Receipt(context, 29) with { StandardOutput = output, StandardError = error };

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal("MIGRATION_EVIDENCE_MISSING", failure.Code);
        Assert.Equal(29, failure.NativeExit);
        Assert.Equal(context.OperationId, failure.OperationId);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, failure.ReceiptFile!)));
        Assert.Equal(29, saved.RootElement.GetProperty("NativeExit").GetInt32());
        Assert.Equal("EVIDENCE_PUBLISH_REJECTED", saved.RootElement.GetProperty("StandardError").GetProperty("Status").GetString());
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
    }

    [Fact]
    public async Task Arbitrary_secret_bearing_native_streams_are_withheld_at_the_producer()
    {
        // Generated solely inside this credential-free unit test; assertions never
        // print the simulated sensitive content, including on failure.
        var simulated = "Password=" + Guid.NewGuid().ToString("N");
        var observation = await ManagedMigrationAdapter.ObserveAsync(new StringReader("provider detail " + simulated));
        var large = await ManagedMigrationAdapter.ObserveAsync(new StringReader(new string('x', 4000) + simulated));
        var mixed = await ManagedMigrationAdapter.ObserveAsync(new StringReader("SUCCESS\n" + simulated));
        var json = JsonSerializer.Serialize(new[] { observation, large, mixed });

        Assert.True(observation.Present && observation.Withheld && observation.Complete);
        Assert.Null(observation.Status);
        Assert.True(large.Present && large.Withheld && large.Complete);
        Assert.True(mixed.Withheld);
        Assert.False(json.Contains(simulated, StringComparison.Ordinal));
        Assert.False(json.Contains("provider detail", StringComparison.Ordinal));
        Assert.Equal(4000 + simulated.Length, large.CharacterCount);
    }

    [Theory]
    [InlineData(0, "SUCCESS", "FORWARD_APPROVED", "not_started")]
    [InlineData(21, "CREDENTIAL_REJECTED", "REJECTED", "not_started")]
    [InlineData(27, "MANIFEST_REJECTED", "FORWARD_APPROVED", "failed")]
    public async Task Published_terminal_record_is_exported_byte_exactly_without_changing_native_exit(
        int exit, string code, string classification, string manifest)
    {
        var context = Context();
        var export = NewRoot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Evidence(context, code, classification, manifest));
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, exit), export);

        Assert.Equal(exit, result.Exit);
        Assert.Equal(classification, result.Classification);
        Assert.Equal(exit == 27, result.ManifestFailed);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        Assert.True(File.Exists(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
        Assert.True(File.Exists(Path.Combine(export, result.ReceiptFile)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("operationId")]
    [InlineData("operation")]
    [InlineData("service")]
    [InlineData("schema")]
    [InlineData("nativeExit")]
    public async Task Uncorrelated_or_inconsistent_terminal_record_is_not_exported(string field)
    {
        var context = Context();
        var export = NewRoot();
        var evidence = Evidence(context);
        evidence = field switch
        {
            "source" => evidence with { SourceCommit = new string('f', 40) },
            "operationId" => evidence with { OperationId = Guid.NewGuid() },
            "operation" => evidence with { Operation = "apply" },
            "service" => evidence with { Service = "client" },
            "schema" => evidence with { SchemaVersion = "unknown" },
            _ => evidence
        };
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(evidence));
        var receipt = Receipt(context, field == "nativeExit" ? 27 : 0);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
        Assert.True(File.Exists(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
    }

    [Theory]
    [InlineData(0, "SUCCESS", "failed")]
    [InlineData(27, "MANIFEST_REJECTED", "not_started")]
    public async Task Native_outcome_and_phase_failure_must_be_consistent(int exit, string code, string manifest)
    {
        var context = Context();
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(Evidence(context, code, "FORWARD_APPROVED", manifest)));

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, exit), export));

        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.Equal(exit, failure.NativeExit);
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
    }

    [Fact]
    public async Task Duplicate_envelope_properties_are_rejected_with_native_receipt_preserved()
    {
        var context = Context();
        var export = NewRoot();
        var json = JsonSerializer.Serialize(Evidence(context));
        var duplicate = json.Insert(1, "\"sourceCommit\":\"" + SourceCommit + "\",");
        await WriteEvidence(context, System.Text.Encoding.UTF8.GetBytes(duplicate));

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export));

        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("caveats")]
    public async Task Unknown_nested_content_cannot_be_exported_under_a_valid_outer_identity(string field)
    {
        var context = Context();
        var export = NewRoot();
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Evidence(context)))!;
        if (field == "roles") node["roles"]!["unexpected"] = "withheld";
        else node["caveats"]!.AsArray().Add("unknown provider detail");
        await WriteEvidence(context, System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export));

        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
    }

    [Fact]
    public async Task Native_start_refusal_retains_unknown_exit_instead_of_inventing_one()
    {
        var context = Context();
        var export = NewRoot();
        var receipt = Receipt(context, null) with { NativeStarted = false, ExecutionFailure = "MIGRATION_PROCESS_START_FAILED" };

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal("MIGRATION_PROCESS_START_FAILED", failure.Code);
        Assert.Null(failure.NativeExit);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, failure.ReceiptFile!)));
        Assert.False(saved.RootElement.GetProperty("NativeStarted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("NativeExit").ValueKind);
    }

    [Fact]
    public async Task Evidence_export_never_overwrites_an_existing_operation_artifact()
    {
        var context = Context();
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(Evidence(context)));
        var path = Path.Combine(export, context.EvidenceFile);
        await File.WriteAllTextAsync(path, "preserved");

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export));

        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.Equal("preserved", await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
    }

    [Fact]
    public async Task Linux_private_root_uses_0700_and_actual_strict_publisher_or_platform_refuses()
    {
        if (!OperatingSystem.IsLinux())
        {
            var failure = Assert.Throws<SetupFailure>(() => ManagedMigrationAdapter.NewOperation("plan", "auth", SourceCommit));
            Assert.Equal("MIGRATION_LINUX_EVIDENCE_REQUIRED", failure.Code);
            return;
        }
        var context = ManagedMigrationAdapter.NewOperation("plan", "auth", SourceCommit);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(context.PrivateRoot));
        var publisher = EvidenceOutput.Create(context.PrivateRoot, context.EvidenceFile);
        await publisher.PublishAsync(Evidence(context));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
        var export = NewRoot();

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export);

        Assert.Equal(0, result.Exit);
        Assert.True((await File.ReadAllBytesAsync(Path.Combine(context.PrivateRoot, context.EvidenceFile)))
            .SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        var second = ManagedMigrationAdapter.NewOperation("plan", "auth", SourceCommit);
        Assert.NotEqual(context.PrivateRoot, second.PrivateRoot);
    }

    private static MigrationOperationContext Context()
    {
        if (OperatingSystem.IsLinux()) return ManagedMigrationAdapter.NewOperation("plan", "auth", SourceCommit);
        return new MigrationOperationContext(Guid.NewGuid(), "plan", "auth", SourceCommit, NewRoot());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "zeka-adapter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WriteEvidence(MigrationOperationContext context, byte[] bytes)
    {
        var path = Path.Combine(context.PrivateRoot, context.EvidenceFile);
        await File.WriteAllBytesAsync(path, bytes);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static NativeReceipt Receipt(MigrationOperationContext context, int? exit) =>
        new(context.OperationId, context.Operation, context.Service, context.SourceCommit, context.PrivateRoot,
            true, exit, null, new(false, 0, null, false, true), new(false, 0, null, false, true));

    private static MigrationEvidence Evidence(MigrationOperationContext context, string code = "SUCCESS",
        string classification = "FORWARD_APPROVED", string manifest = "not_started") =>
        new("zeka-db-migrate-result/v0.1.0", "0.1.0", context.SourceCommit, context.OperationId, context.Operation,
            context.Service, "AuthManagement", "latest", null, null, null, classification, code, false,
            new RoleAssertions("zeka_auth_migrator", null, "zeka_auth_owner", null, false, false, false),
            new PhaseResults(new("not_started"), new("not_started"), new(manifest), new("untested")),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, []);
}
