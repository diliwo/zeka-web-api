using System.Text.Json;
using System.Text.Json.Nodes;
using Zeka.LocalStack.DatabaseSetup;
using Xunit;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-17")]
[Trait("Evidence", "LocalStack")]
public sealed class ManagedMigrationAdapterTests
{
    private const string SourceCommit = "0123456789abcdef0123456789abcdef01234567";
    private const string AuthPredecessor = AuthOnboardingSqlArtifacts.FromMigration;
    private const string AuthTarget = AuthOnboardingSqlArtifacts.TargetMigration;
    private const string FixtureDatabase = "fixture_auth";

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_atomic_terminal_record_preserves_acknowledged_fresh_and_current_state(bool alreadyCurrent)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = AtomicEvidence(context, alreadyCurrent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export);

        Assert.Equal(0, result.Exit);
        Assert.Equal(evidence.Classification, result.Classification);
        Assert.Equal(AuthTarget, result.RequestedTarget);
        Assert.Equal("SUCCESS", result.ResultCode);
        Assert.True(result.AtomicAuth);
        Assert.Equal(alreadyCurrent, result.IsAcknowledgedCurrent);
        Assert.Equal(!alreadyCurrent, result.MigrationPassed);
        Assert.Equal(alreadyCurrent ? AuthTarget : AuthPredecessor, result.StartingMigration);
        Assert.Equal(AuthTarget, result.ObservedFinalMigration);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, result.ReceiptFile)));
        Assert.Equal(AuthTarget, native.RootElement.GetProperty("RequestedTarget").GetString());
        Assert.Equal(FixtureDatabase, native.RootElement.GetProperty("ExpectedDatabase").GetString());
    }

    [Fact]
    public async Task Exact_onboarding_plan_uses_the_legacy_envelope_without_claiming_atomic_apply()
    {
        var context = Context(requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(Evidence(context)));

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export);

        Assert.Equal(AuthTarget, result.RequestedTarget);
        Assert.Equal("FORWARD_APPROVED", result.Classification);
        Assert.False(result.AtomicAuth);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.False(result.MigrationPassed);
    }

    [Theory]
    [InlineData("auth", AuthPredecessor, "untested")]
    [InlineData("adminarea", "latest", "passed")]
    [InlineData("client", "latest", "passed")]
    public async Task Existing_legacy_current_results_keep_their_service_and_target_contract(
        string service, string target, string manifest)
    {
        var context = Context("apply", service, target);
        var export = NewRoot();
        var evidence = Evidence(context, classification: "ALREADY_CURRENT", manifest: manifest);
        evidence = evidence with { StartingMigration = evidence.ResolvedTarget, ObservedFinalMigration = evidence.ResolvedTarget };
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(evidence));

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 0), export);

        Assert.Equal(target, result.RequestedTarget);
        Assert.Equal("ALREADY_CURRENT", result.Classification);
        Assert.False(result.AtomicAuth);
        Assert.True(result.IsAcknowledgedCurrent);
        Assert.False(result.MigrationPassed);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("operationId")]
    [InlineData("operation")]
    [InlineData("service")]
    [InlineData("schema")]
    [InlineData("logicalDatabase")]
    [InlineData("requestedTarget")]
    [InlineData("resolvedTarget")]
    [InlineData("startingMigration")]
    [InlineData("observedFinalMigration")]
    [InlineData("classification")]
    [InlineData("resultCode")]
    [InlineData("advisoryLock")]
    [InlineData("expectedSessionUser")]
    [InlineData("observedSessionUser")]
    [InlineData("expectedCurrentUser")]
    [InlineData("observedCurrentUser")]
    [InlineData("restrictedAttributes")]
    [InlineData("matchingOwnerMembership")]
    [InlineData("ownerAssumption")]
    [InlineData("migrationHash")]
    [InlineData("grantHash")]
    [InlineData("manifestHash")]
    [InlineData("databaseName")]
    [InlineData("databaseNull")]
    [InlineData("backendPid")]
    [InlineData("backendPidNull")]
    [InlineData("commitAttempted")]
    [InlineData("commitAcknowledged")]
    [InlineData("rollbackConfirmed")]
    [InlineData("durableState")]
    [InlineData("replayAttempted")]
    [InlineData("failedStage")]
    [InlineData("migrationPhase")]
    [InlineData("grantPhase")]
    [InlineData("manifestPhase")]
    [InlineData("runtimePhase")]
    [InlineData("endedAt")]
    [InlineData("extraEnvelope")]
    [InlineData("extraAtomic")]
    [InlineData("extraPhase")]
    [InlineData("extraRole")]
    [InlineData("unknownCaveat")]
    public async Task Forged_atomic_success_is_rejected_before_terminal_export(string field)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var node = JsonNode.Parse(JsonSerializer.Serialize(AtomicEvidence(context)))!;
        MutateAtomicSuccess(node, field);
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Theory]
    [InlineData("atomicAuth")]
    [InlineData("atomicAuth.databaseName")]
    [InlineData("atomicAuth.backendPid")]
    [InlineData("atomicAuth.commitAcknowledged")]
    [InlineData("phases.manifestVerification")]
    [InlineData("roles.ownerAssumption")]
    public async Task Missing_atomic_fields_cannot_be_interpreted_as_success(string field)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var node = JsonNode.Parse(JsonSerializer.Serialize(AtomicEvidence(context)))!;
        var parts = field.Split('.');
        if (parts.Length == 1) node.AsObject().Remove(parts[0]);
        else node[parts[0]]!.AsObject().Remove(parts[1]);
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Fact]
    public async Task Duplicate_nested_atomic_fields_are_rejected_without_replacing_the_native_exit()
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var json = JsonSerializer.Serialize(AtomicEvidence(context));
        var duplicate = json.Replace("\"atomicAuth\":{", "\"atomicAuth\":{\"databaseName\":\"fixture_auth\",", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate);
        await WriteEvidence(context, System.Text.Encoding.UTF8.GetBytes(duplicate));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Theory]
    [InlineData("plan", "auth", AuthTarget)]
    [InlineData("apply", "auth", AuthPredecessor)]
    [InlineData("apply", "auth", "latest")]
    [InlineData("apply", "adminarea", AuthTarget)]
    [InlineData("apply", "client", AuthTarget)]
    public async Task Atomic_shape_is_never_adopted_for_a_legacy_operation(string operation, string service, string target)
    {
        var context = Context(operation, service, target, FixtureDatabase);
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(AtomicEvidence(context)));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("database")]
    public async Task Native_receipt_binds_the_exact_target_and_expected_database(string field)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(AtomicEvidence(context)));
        var receipt = Receipt(context, 0);
        receipt = field == "target" ? receipt with { RequestedTarget = AuthPredecessor }
            : receipt with { ExpectedDatabase = "other_fixture" };

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal("MIGRATION_RECEIPT_IDENTITY_REJECTED", failure.Code);
        Assert.Equal(0, failure.NativeExit);
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
        Assert.True(File.Exists(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
    }

    [Theory]
    [InlineData(false, 27)]
    [InlineData(true, 28)]
    public async Task Correlated_nonzero_atomic_failures_remain_byte_exact_failures(bool inspection, int exit)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = AtomicFailure(context, inspection);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, exit), export);

        Assert.Equal(exit, result.Exit);
        Assert.Equal(evidence.Classification, result.Classification);
        Assert.Equal(evidence.ResultCode, result.ResultCode);
        Assert.True(result.AtomicAuth);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        Assert.True(File.Exists(Path.Combine(export, result.ReceiptFile)));
    }

    public static IEnumerable<object[]> ProducerPrecommitFailureCases()
    {
        // These are source-defined terminal states, not executed database evidence.
        yield return ["ARTIFACT_BINDING", "REJECTED", "ATOMIC_AUTH_EXACT_TARGET_REQUIRED", 20, false, false];
        yield return ["ARTIFACT_BINDING", "REJECTED", "ATOMIC_AUTH_DATABASE_BINDING_REQUIRED", 20, false, false];
        yield return ["ARTIFACT_BINDING", "REJECTED", "ATOMIC_AUTH_FAILED", 27, false, false];
        yield return ["CONNECTION", "REJECTED", "ATOMIC_AUTH_POSTGRESQL17_REQUIRED", 24, false, false];
        yield return ["CONNECTION", "REJECTED", "ATOMIC_AUTH_FAILED", 27, false, false];
        yield return ["TRANSACTION", "REJECTED", "ATOMIC_AUTH_FAILED", 27, false, false];
        var stages = new (string Stage, string Code, int Exit, bool Current, bool AfterPhase)[]
        {
            ("TRANSACTION", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("ADVISORY_TRANSACTION_LOCK", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("ADVISORY_TRANSACTION_LOCK", "ATOMIC_AUTH_FAILED", 27, false, true),
            ("ROLE_IDENTITY", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("ROLE_IDENTITY", "ATOMIC_AUTH_FAILED", 27, false, true),
            ("ROLE_IDENTITY", "ATOMIC_AUTH_IDENTITY_REJECTED", 24, false, false),
            ("ROLE_IDENTITY", "ATOMIC_AUTH_OWNER_ASSUMPTION_REJECTED", 24, false, false),
            ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_COMPILED_HISTORY_REJECTED", 25, false, false),
            ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_HISTORY_REJECTED", 25, false, false),
            ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED", 25, false, false),
            ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED", 27, false, false),
            ("MIGRATION_SQL", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("APPROVED_GRANTS", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("APPROVED_GRANTS", "ATOMIC_AUTH_FAILED", 27, false, true),
            ("EXACT_SCHEMA_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("EXACT_SCHEMA_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, true, false),
            ("EXACT_SCHEMA_VERIFICATION", "ATOMIC_AUTH_FINAL_HISTORY_REJECTED", 25, false, false),
            ("EXACT_SCHEMA_VERIFICATION", "ATOMIC_AUTH_FINAL_HISTORY_REJECTED", 25, true, false),
            ("FULL_V20_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, false, false),
            ("FULL_V20_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, true, false),
            ("FULL_V20_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, false, true),
            ("FULL_V20_VERIFICATION", "ATOMIC_AUTH_FAILED", 27, true, true)
        };
        foreach (var row in stages)
        {
            yield return [row.Stage, "ATOMIC_AUTH_ROLLED_BACK", row.Code, row.Exit, row.Current, row.AfterPhase];
            yield return [row.Stage, "ATOMIC_AUTH_REQUIRES_INSPECTION", row.Code, 28, row.Current, row.AfterPhase];
        }
    }

    [Theory]
    [MemberData(nameof(ProducerPrecommitFailureCases))]
    public async Task Producer_precommit_failure_states_preserve_native_exit_and_byte_exact_evidence(
        string stage, string classification, string code, int exit, bool current, bool afterPhase)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = PrecommitFailure(context, stage, classification, code, current, afterPhase);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, exit), export);

        Assert.Equal(exit, result.Exit);
        Assert.Equal(classification, result.Classification);
        Assert.Equal(code, result.ResultCode);
        Assert.True(result.AtomicAuth);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.False(result.MigrationPassed && classification == "ATOMIC_AUTH_ROLLED_BACK");
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        Assert.True(File.Exists(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
        using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, result.ReceiptFile)));
        Assert.Equal(exit, native.RootElement.GetProperty("NativeExit").GetInt32());
    }

    [Theory]
    [InlineData(false, "INCONCLUSIVE")]
    [InlineData(false, "BASELINE_VERIFIED")]
    [InlineData(false, "COMMITTED_VERIFIED")]
    [InlineData(true, "INCONCLUSIVE")]
    [InlineData(true, "BASELINE_VERIFIED")]
    [InlineData(true, "COMMITTED_VERIFIED")]
    public async Task Unacknowledged_commit_keeps_inspection_after_every_durable_readback(bool current, string durable)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = CommitFailure(context, current, durable);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 28), export);

        Assert.Equal(28, result.Exit);
        Assert.Equal("ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION", result.Classification);
        Assert.Equal("ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION", result.ResultCode);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, result.ReceiptFile)));
        Assert.Equal(28, native.RootElement.GetProperty("NativeExit").GetInt32());
    }

    [Theory]
    [InlineData("chiefRejectedCommit")]
    [InlineData("rejectedRollback")]
    [InlineData("rejectedInspectionDurable")]
    [InlineData("rejectedAfterTransaction")]
    [InlineData("rejectedArtifactProgress")]
    [InlineData("rejectedArtifactDatabase")]
    [InlineData("rejectedConnectionHistory")]
    [InlineData("rejectedConnectionMissingDatabase")]
    [InlineData("transactionMissingPid")]
    [InlineData("rollbackNative28")]
    [InlineData("rollbackMigrationPassed")]
    [InlineData("rollbackGrantPassed")]
    [InlineData("rollbackManifestRolledBack")]
    [InlineData("rollbackCommitStage")]
    [InlineData("rollbackWrongCodeStage")]
    [InlineData("rollbackEarlyRefusalCode")]
    [InlineData("rollbackFinalBeforeVerification")]
    [InlineData("inspectionRollbackTrue")]
    [InlineData("inspectionRollbackNull")]
    [InlineData("inspectionDurableInconclusive")]
    [InlineData("inspectionRolledBackMigration")]
    [InlineData("inspectionRolledBackGrant")]
    [InlineData("inspectionWrongNative")]
    [InlineData("inspectionCommitCode")]
    [InlineData("migrationGrantProgress")]
    [InlineData("grantsMissingMigration")]
    [InlineData("schemaMixedFreshCurrent")]
    [InlineData("schemaManifestProgress")]
    [InlineData("fullManifestNotStarted")]
    [InlineData("fullFinalMissing")]
    [InlineData("currentWrongStarting")]
    [InlineData("commitWrongCode")]
    [InlineData("commitWrongNative")]
    [InlineData("commitRollbackTrue")]
    [InlineData("commitRollbackFalse")]
    [InlineData("commitNotAttempted")]
    [InlineData("commitAcknowledged")]
    [InlineData("commitWrongStage")]
    [InlineData("commitNotChecked")]
    [InlineData("commitRolledBack")]
    [InlineData("commitManifestFailed")]
    [InlineData("commitMigrationRolledBack")]
    [InlineData("commitMixedPhases")]
    [InlineData("commitMissingFinal")]
    public async Task Contradictory_failure_states_are_rejected_with_the_real_native_receipt_preserved(string scenario)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = scenario.StartsWith("commit", StringComparison.Ordinal) || scenario == "chiefRejectedCommit"
            ? CommitFailure(context, false, "INCONCLUSIVE")
            : scenario.StartsWith("rejected", StringComparison.Ordinal)
                ? PrecommitFailure(context, "ARTIFACT_BINDING", "REJECTED", "ATOMIC_AUTH_FAILED")
                : scenario.StartsWith("inspection", StringComparison.Ordinal)
                    ? PrecommitFailure(context, "FULL_V20_VERIFICATION", "ATOMIC_AUTH_REQUIRES_INSPECTION", "ATOMIC_AUTH_FAILED")
                    : AtomicFailure(context, false);
        var exit = evidence.Classification is "ATOMIC_AUTH_REQUIRES_INSPECTION" or "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION" ? 28 : 27;
        var node = JsonNode.Parse(JsonSerializer.Serialize(evidence))!;
        var atomic = node["atomicAuth"]!;
        var phases = node["phases"]!;
        switch (scenario)
        {
            case "chiefRejectedCommit": node["classification"] = "REJECTED"; node["resultCode"] = "ATOMIC_AUTH_FAILED"; exit = 27; break;
            case "rejectedRollback": atomic["rollbackConfirmed"] = true; break;
            case "rejectedInspectionDurable": atomic["durableState"] = "INCONCLUSIVE"; break;
            case "rejectedAfterTransaction": atomic["failedStage"] = "ADVISORY_TRANSACTION_LOCK"; break;
            case "rejectedArtifactProgress": phases["migration"]!["status"] = "started"; break;
            case "rejectedArtifactDatabase": atomic["databaseName"] = FixtureDatabase; break;
            case "rejectedConnectionHistory": atomic["failedStage"] = "CONNECTION"; atomic["databaseName"] = FixtureDatabase; node["startingMigration"] = AuthPredecessor; break;
            case "rejectedConnectionMissingDatabase": atomic["failedStage"] = "CONNECTION"; break;
            case "transactionMissingPid": atomic["failedStage"] = "TRANSACTION"; atomic["backendPid"] = null; break;
            case "rollbackNative28": exit = 28; break;
            case "rollbackMigrationPassed": phases["migration"]!["status"] = "passed"; break;
            case "rollbackGrantPassed": phases["bootstrapReconciliation"]!["status"] = "passed"; break;
            case "rollbackManifestRolledBack": phases["manifestVerification"]!["status"] = "rolled_back"; break;
            case "rollbackCommitStage": atomic["failedStage"] = "COMMIT"; break;
            case "rollbackWrongCodeStage": node["resultCode"] = "ATOMIC_AUTH_IDENTITY_REJECTED"; exit = 24; break;
            case "rollbackEarlyRefusalCode": node["resultCode"] = "ATOMIC_AUTH_EXACT_TARGET_REQUIRED"; exit = 20; break;
            case "rollbackFinalBeforeVerification": atomic["failedStage"] = "HISTORY_AND_SCHEMA_RECHECK"; node["observedFinalMigration"] = AuthTarget; break;
            case "inspectionRollbackTrue": atomic["rollbackConfirmed"] = true; break;
            case "inspectionRollbackNull": atomic["rollbackConfirmed"] = null; break;
            case "inspectionDurableInconclusive": atomic["durableState"] = "INCONCLUSIVE"; break;
            case "inspectionRolledBackMigration": phases["migration"]!["status"] = "rolled_back"; break;
            case "inspectionRolledBackGrant": phases["bootstrapReconciliation"]!["status"] = "rolled_back"; break;
            case "inspectionWrongNative": exit = 27; break;
            case "inspectionCommitCode": node["resultCode"] = "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION"; break;
            case "migrationGrantProgress": atomic["failedStage"] = "MIGRATION_SQL"; phases["bootstrapReconciliation"]!["status"] = "rolled_back"; break;
            case "grantsMissingMigration": atomic["failedStage"] = "APPROVED_GRANTS"; phases["migration"]!["status"] = "not_started"; break;
            case "schemaMixedFreshCurrent": atomic["failedStage"] = "EXACT_SCHEMA_VERIFICATION"; phases["bootstrapReconciliation"]!["status"] = "not_needed"; phases["manifestVerification"]!["status"] = "not_started"; break;
            case "schemaManifestProgress": atomic["failedStage"] = "EXACT_SCHEMA_VERIFICATION"; break;
            case "fullManifestNotStarted": phases["manifestVerification"]!["status"] = "not_started"; break;
            case "fullFinalMissing": node["observedFinalMigration"] = null; break;
            case "currentWrongStarting": phases["migration"]!["status"] = "not_needed"; phases["bootstrapReconciliation"]!["status"] = "not_needed"; break;
            case "commitWrongCode": node["resultCode"] = "ATOMIC_AUTH_FAILED"; break;
            case "commitWrongNative": exit = 27; break;
            case "commitRollbackTrue": atomic["rollbackConfirmed"] = true; break;
            case "commitRollbackFalse": atomic["rollbackConfirmed"] = false; break;
            case "commitNotAttempted": atomic["commitAttempted"] = false; break;
            case "commitAcknowledged": atomic["commitAcknowledged"] = true; break;
            case "commitWrongStage": atomic["failedStage"] = "FULL_V20_VERIFICATION"; break;
            case "commitNotChecked": atomic["durableState"] = "NOT_CHECKED"; break;
            case "commitRolledBack": atomic["durableState"] = "ROLLED_BACK"; break;
            case "commitManifestFailed": phases["manifestVerification"]!["status"] = "failed"; break;
            case "commitMigrationRolledBack": phases["migration"]!["status"] = "rolled_back"; break;
            case "commitMixedPhases": phases["bootstrapReconciliation"]!["status"] = "not_needed"; break;
            case "commitMissingFinal": node["observedFinalMigration"] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, exit), export);
    }

    [Theory]
    [InlineData("ATOMIC_AUTH_ROLLED_BACK", 27)]
    [InlineData("ATOMIC_AUTH_REQUIRES_INSPECTION", 28)]
    public async Task Initial_role_read_failure_preserves_unobserved_identity_without_claiming_owner_authority(
        string classification, int exit)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = PrecommitFailure(context, "ROLE_IDENTITY", classification, "ATOMIC_AUTH_FAILED");
        evidence = evidence with
        {
            Roles = new RoleAssertions("zeka_auth_migrator", null, "zeka_auth_owner", null, false, false, false)
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, exit), export);

        Assert.Equal(exit, result.Exit);
        Assert.Equal(classification, result.Classification);
        Assert.Equal("ATOMIC_AUTH_FAILED", result.ResultCode);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        using var native = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, result.ReceiptFile)));
        Assert.Equal(exit, native.RootElement.GetProperty("NativeExit").GetInt32());
    }

    [Theory]
    [InlineData("initialIdentityActuallyAccepted", 24)]
    [InlineData("ownerActuallyAssumed", 24)]
    [InlineData("ownerFlagWithWrongPair", 27)]
    [InlineData("ownerFlagWithoutRestriction", 27)]
    [InlineData("ownerPairWithoutComputedFlag", 27)]
    [InlineData("partialInitialObservation", 27)]
    public async Task Role_refusal_or_assumption_flags_cannot_contradict_the_observed_identity(string scenario, int exit)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = PrecommitFailure(context, "ROLE_IDENTITY", "ATOMIC_AUTH_ROLLED_BACK", "ATOMIC_AUTH_FAILED");
        var node = JsonNode.Parse(JsonSerializer.Serialize(evidence))!;
        var roles = node["roles"]!;
        switch (scenario)
        {
            case "initialIdentityActuallyAccepted": node["resultCode"] = "ATOMIC_AUTH_IDENTITY_REJECTED"; break;
            case "ownerActuallyAssumed":
                node["resultCode"] = "ATOMIC_AUTH_OWNER_ASSUMPTION_REJECTED";
                roles["observedCurrentUser"] = "zeka_auth_owner";
                break;
            case "ownerFlagWithWrongPair": roles["ownerAssumption"] = true; break;
            case "ownerPairWithoutComputedFlag": roles["observedCurrentUser"] = "zeka_auth_owner"; break;
            case "partialInitialObservation": roles["observedCurrentUser"] = null; break;
            case "ownerFlagWithoutRestriction":
                roles["observedCurrentUser"] = "zeka_auth_owner";
                roles["ownerAssumption"] = true;
                roles["restrictedAttributes"] = false;
                break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, exit), export);
    }

    [Theory]
    [InlineData("inspection")]
    [InlineData("rolledBack")]
    [InlineData("cancelled")]
    public async Task Zero_exit_cannot_turn_inspection_rollback_or_cancellation_into_current(string state)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = state == "inspection" ? AtomicFailure(context, true) : AtomicFailure(context, false);
        if (state == "cancelled") evidence = evidence with { ResultCode = "OPERATION_CANCELLED" };
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(evidence));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Theory]
    [InlineData("MIGRATION_PROCESS_CANCELLED")]
    [InlineData("MIGRATION_NATIVE_STREAM_INCOMPLETE")]
    public async Task Native_cancellation_or_incomplete_drain_preserves_evidence_without_returning_current(string code)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(AtomicEvidence(context, true));
        await WriteEvidence(context, bytes);
        var receipt = Receipt(context, 0) with { ExecutionFailure = code };
        if (code == "MIGRATION_NATIVE_STREAM_INCOMPLETE")
            receipt = receipt with { StandardOutput = new(true, null, null, true, false) };

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal(code, failure.Code);
        Assert.Equal(0, failure.NativeExit);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
    }

    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(30)]
    public async Task Successful_atomic_envelope_cannot_override_a_nonzero_native_exit(int exit)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(AtomicEvidence(context, true)));

        await AssertTerminalRejectedAsync(context, Receipt(context, exit), export);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("commitAttempted")]
    [InlineData("commitAcknowledged")]
    [InlineData("durable")]
    [InlineData("failedStage")]
    [InlineData("replay")]
    [InlineData("nativeExit")]
    public async Task Forged_nonzero_atomic_failure_is_not_exported_as_a_valid_terminal_record(string field)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var node = JsonNode.Parse(JsonSerializer.Serialize(AtomicFailure(context, false)))!;
        var atomic = node["atomicAuth"]!;
        switch (field)
        {
            case "rollback": atomic["rollbackConfirmed"] = false; break;
            case "commitAttempted": atomic["commitAttempted"] = true; break;
            case "commitAcknowledged": atomic["commitAcknowledged"] = true; break;
            case "durable": atomic["durableState"] = "BASELINE_VERIFIED"; break;
            case "failedStage": atomic["failedStage"] = null; break;
            case "replay": atomic["replayAttempted"] = true; break;
            case "nativeExit": break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, field == "nativeExit" ? 30 : 27), export);
    }

    [Fact]
    public async Task Durable_committed_observation_without_commit_acknowledgement_remains_inspection()
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var evidence = AtomicFailure(context, true);
        evidence = evidence with { AtomicAuth = evidence.AtomicAuth! with { DurableState = "COMMITTED_VERIFIED" } };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 28), export);

        Assert.Equal(28, result.Exit);
        Assert.Equal("ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION", result.Classification);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
    }

    [Fact]
    public async Task Incomplete_native_stream_without_a_failure_marker_cannot_return_current()
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(AtomicEvidence(context, true));
        await WriteEvidence(context, bytes);
        var receipt = Receipt(context, 0) with { StandardError = new(true, null, null, true, false) };
        Assert.Null(receipt.ExecutionFailure);

        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));

        Assert.Equal("MIGRATION_NATIVE_STREAM_INCOMPLETE", failure.Code);
        Assert.Equal(0, failure.NativeExit);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(export, failure.ReceiptFile!)));
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("ExecutionFailure").ValueKind);
        Assert.False(saved.RootElement.GetProperty("StandardError").GetProperty("Complete").GetBoolean());
    }

    [Theory]
    [InlineData("adminarea", "latest")]
    [InlineData("client", "latest")]
    [InlineData("auth", "latest")]
    [InlineData("auth", AuthTarget)]
    public async Task Current_final_target_cannot_omit_manifest_verification(string service, string target)
    {
        var context = Context(service: service, requestedTarget: target);
        var export = NewRoot();
        var evidence = Evidence(context, classification: "ALREADY_CURRENT", manifest: "untested");
        evidence = evidence with { StartingMigration = evidence.ResolvedTarget, ObservedFinalMigration = evidence.ResolvedTarget };
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(evidence));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    [Fact]
    public async Task Genuine_pre_connection_credential_refusal_for_exact_atomic_request_keeps_its_legacy_shape()
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Evidence(context, "CREDENTIAL_REJECTED", "REJECTED"));
        await WriteEvidence(context, bytes);

        var result = await ManagedMigrationAdapter.CompleteAsync(context, Receipt(context, 21), export);

        Assert.Equal(21, result.Exit);
        Assert.Equal(AuthTarget, result.RequestedTarget);
        Assert.Equal("REJECTED", result.Classification);
        Assert.Equal("CREDENTIAL_REJECTED", result.ResultCode);
        Assert.False(result.AtomicAuth);
        Assert.False(result.IsAcknowledgedCurrent);
        Assert.False(result.MigrationPassed);
        Assert.Null(result.StartingMigration);
        Assert.Null(result.ObservedFinalMigration);
        Assert.True(bytes.SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(export, context.EvidenceFile))));
        Assert.True(File.Exists(Path.Combine(export, result.ReceiptFile)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("operationId")]
    [InlineData("operation")]
    [InlineData("service")]
    [InlineData("requestedTarget")]
    [InlineData("classification")]
    [InlineData("resultCode")]
    [InlineData("startingMigration")]
    [InlineData("resolvedTarget")]
    [InlineData("observedFinalMigration")]
    [InlineData("advisoryLock")]
    [InlineData("expectedSessionUser")]
    [InlineData("expectedCurrentUser")]
    [InlineData("observedSessionUser")]
    [InlineData("observedCurrentUser")]
    [InlineData("restrictedAttributes")]
    [InlineData("matchingOwnerMembership")]
    [InlineData("ownerAssumption")]
    [InlineData("migrationPhase")]
    [InlineData("bootstrapPhase")]
    [InlineData("manifestPhase")]
    [InlineData("runtimePhase")]
    [InlineData("emptyCaveats")]
    [InlineData("extraCaveat")]
    [InlineData("differentCaveat")]
    [InlineData("atomicAuth")]
    [InlineData("postgresDiagnostic")]
    [InlineData("extraEnvelope")]
    public async Task Pre_connection_refusal_fallback_rejects_any_claim_of_progress_or_extra_authority(string field)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        var node = JsonNode.Parse(JsonSerializer.Serialize(Evidence(context, "CREDENTIAL_REJECTED", "REJECTED")))!;
        var roles = node["roles"]!;
        var phases = node["phases"]!;
        switch (field)
        {
            case "source": node["sourceCommit"] = new string('f', 40); break;
            case "operationId": node["operationId"] = Guid.NewGuid(); break;
            case "operation": node["operation"] = "plan"; break;
            case "service": node["service"] = "client"; break;
            case "requestedTarget": node["requestedTarget"] = AuthPredecessor; break;
            case "classification": node["classification"] = "FORWARD_APPROVED"; break;
            case "resultCode": node["resultCode"] = "SUCCESS"; break;
            case "startingMigration": node["startingMigration"] = AuthPredecessor; break;
            case "resolvedTarget": node["resolvedTarget"] = AuthTarget; break;
            case "observedFinalMigration": node["observedFinalMigration"] = AuthTarget; break;
            case "advisoryLock": node["advisoryLockAcquired"] = true; break;
            case "expectedSessionUser": roles["expectedSessionUser"] = "zeka_other_migrator"; break;
            case "expectedCurrentUser": roles["expectedCurrentUser"] = "zeka_other_owner"; break;
            case "observedSessionUser": roles["observedSessionUser"] = "zeka_auth_migrator"; break;
            case "observedCurrentUser": roles["observedCurrentUser"] = "zeka_auth_owner"; break;
            case "restrictedAttributes": roles["restrictedAttributes"] = true; break;
            case "matchingOwnerMembership": roles["matchingOwnerMembership"] = true; break;
            case "ownerAssumption": roles["ownerAssumption"] = true; break;
            case "migrationPhase": phases["migration"]!["status"] = "passed"; break;
            case "bootstrapPhase": phases["bootstrapReconciliation"]!["status"] = "passed"; break;
            case "manifestPhase": phases["manifestVerification"]!["status"] = "failed"; break;
            case "runtimePhase": phases["runtimeReadiness"]!["status"] = "not_started"; break;
            case "emptyCaveats": node["caveats"]!.AsArray().Clear(); break;
            case "extraCaveat": node["caveats"]!.AsArray().Add("Plan mode performed no persistent migration mutation."); break;
            case "differentCaveat": node["caveats"]![0] = "Plan mode performed no persistent migration mutation."; break;
            case "atomicAuth": node["atomicAuth"] = null; break;
            case "postgresDiagnostic":
                node["postgresDiagnostic"] = new JsonObject
                {
                    ["sqlState"] = "42501", ["message"] = "Synthetic denied operation", ["hint"] = null, ["position"] = 0
                };
                break;
            case "extraEnvelope": node["unexpected"] = "withheld"; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(node));

        await AssertTerminalRejectedAsync(context, Receipt(context, 21), export);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(24)]
    [InlineData(27)]
    public async Task Pre_connection_credential_refusal_requires_its_exact_nonzero_native_exit(int exit)
    {
        var context = Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase);
        var export = NewRoot();
        await WriteEvidence(context, JsonSerializer.SerializeToUtf8Bytes(Evidence(context, "CREDENTIAL_REJECTED", "REJECTED")));

        await AssertTerminalRejectedAsync(context, Receipt(context, exit), export);
    }

    [Theory]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "[]")]
    [InlineData(true, "[]")]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    public async Task Malformed_or_non_envelope_json_preserves_native_receipt_without_export(bool atomic, string json)
    {
        var context = atomic
            ? Context("apply", requestedTarget: AuthTarget, expectedDatabase: FixtureDatabase)
            : Context();
        var export = NewRoot();
        await WriteEvidence(context, System.Text.Encoding.UTF8.GetBytes(json));

        await AssertTerminalRejectedAsync(context, Receipt(context, 0), export);
    }

    private static async Task AssertTerminalRejectedAsync(MigrationOperationContext context, NativeReceipt receipt, string export)
    {
        var failure = await Assert.ThrowsAsync<SetupFailure>(() => ManagedMigrationAdapter.CompleteAsync(context, receipt, export));
        Assert.Equal("MIGRATION_EVIDENCE_REJECTED", failure.Code);
        Assert.Equal(receipt.NativeExit, failure.NativeExit);
        Assert.True(File.Exists(Path.Combine(export, failure.ReceiptFile!)));
        Assert.False(File.Exists(Path.Combine(export, context.EvidenceFile)));
        Assert.True(File.Exists(Path.Combine(context.PrivateRoot, context.EvidenceFile)));
    }

    private static void MutateAtomicSuccess(JsonNode node, string field)
    {
        var roles = node["roles"]!;
        var atomic = node["atomicAuth"]!;
        var phases = node["phases"]!;
        switch (field)
        {
            case "source": node["sourceCommit"] = new string('f', 40); break;
            case "operationId": node["operationId"] = Guid.NewGuid(); break;
            case "operation": node["operation"] = "plan"; break;
            case "service": node["service"] = "client"; break;
            case "schema": node["schemaVersion"] = "unknown"; break;
            case "logicalDatabase": node["logicalDatabase"] = "ClientManagement"; break;
            case "requestedTarget": node["requestedTarget"] = AuthPredecessor; break;
            case "resolvedTarget": node["resolvedTarget"] = AuthPredecessor; break;
            case "startingMigration": node["startingMigration"] = AuthTarget; break;
            case "observedFinalMigration": node["observedFinalMigration"] = AuthPredecessor; break;
            case "classification": node["classification"] = "ALREADY_CURRENT"; break;
            case "resultCode": node["resultCode"] = "ATOMIC_AUTH_FAILED"; break;
            case "advisoryLock": node["advisoryLockAcquired"] = false; break;
            case "expectedSessionUser": roles["expectedSessionUser"] = "zeka_other_migrator"; break;
            case "observedSessionUser": roles["observedSessionUser"] = "zeka_other_migrator"; break;
            case "expectedCurrentUser": roles["expectedCurrentUser"] = "zeka_other_owner"; break;
            case "observedCurrentUser": roles["observedCurrentUser"] = "zeka_other_owner"; break;
            case "restrictedAttributes": roles["restrictedAttributes"] = false; break;
            case "matchingOwnerMembership": roles["matchingOwnerMembership"] = false; break;
            case "ownerAssumption": roles["ownerAssumption"] = false; break;
            case "migrationHash": atomic["migrationSqlSha256"] = new string('f', 64); break;
            case "grantHash": atomic["grantSqlSha256"] = new string('f', 64); break;
            case "manifestHash": atomic["manifestSha256"] = new string('f', 64); break;
            case "databaseName": atomic["databaseName"] = "other_fixture"; break;
            case "databaseNull": atomic["databaseName"] = null; break;
            case "backendPid": atomic["backendPid"] = 0; break;
            case "backendPidNull": atomic["backendPid"] = null; break;
            case "commitAttempted": atomic["commitAttempted"] = false; break;
            case "commitAcknowledged": atomic["commitAcknowledged"] = false; break;
            case "rollbackConfirmed": atomic["rollbackConfirmed"] = true; break;
            case "durableState": atomic["durableState"] = "INCONCLUSIVE"; break;
            case "replayAttempted": atomic["replayAttempted"] = true; break;
            case "failedStage": atomic["failedStage"] = "COMMIT"; break;
            case "migrationPhase": phases["migration"]!["status"] = "not_needed"; break;
            case "grantPhase": phases["bootstrapReconciliation"]!["status"] = "not_needed"; break;
            case "manifestPhase": phases["manifestVerification"]!["status"] = "not_started"; break;
            case "runtimePhase": phases["runtimeReadiness"]!["status"] = "passed"; break;
            case "endedAt": node["endedAt"] = DateTimeOffset.UnixEpoch.AddSeconds(-1); break;
            case "extraEnvelope": node["unexpected"] = "withheld"; break;
            case "extraAtomic": atomic["unexpected"] = "withheld"; break;
            case "extraPhase": phases["manifestVerification"]!["unexpected"] = "withheld"; break;
            case "extraRole": roles["unexpected"] = "withheld"; break;
            case "unknownCaveat": node["caveats"]!.AsArray().Add("unapproved provider detail"); break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private static MigrationEvidence AtomicEvidence(MigrationOperationContext context, bool alreadyCurrent = false) =>
        new("zeka-db-migrate-result/v0.1.0", "0.1.0", context.SourceCommit, context.OperationId, context.Operation,
            context.Service, LogicalDatabase(context.Service), context.RequestedTarget,
            alreadyCurrent ? AuthTarget : AuthPredecessor, AuthTarget, AuthTarget,
            alreadyCurrent ? "ATOMIC_AUTH_ALREADY_CURRENT" : "ATOMIC_AUTH_APPLIED", "SUCCESS", true,
            SuccessfulRoles(context.Service),
            new PhaseResults(new(alreadyCurrent ? "not_needed" : "passed"), new(alreadyCurrent ? "not_needed" : "passed"),
                new("passed"), new("untested")), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [])
        {
            AtomicAuth = new AtomicAuthEvidence(AuthOnboardingSqlArtifacts.MigrationSha256,
                AuthOnboardingSqlArtifacts.GrantSha256, AuthOnboardingSqlArtifacts.ManifestSha256,
                true, true, null, "COMMITTED_VERIFIED", false, FixtureDatabase, 42, null)
        };

    private static MigrationEvidence AtomicFailure(MigrationOperationContext context, bool inspection)
    {
        var valid = AtomicEvidence(context);
        return valid with
        {
            Classification = inspection ? "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION" : "ATOMIC_AUTH_ROLLED_BACK",
            ResultCode = inspection ? "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION" : "ATOMIC_AUTH_FAILED",
            ObservedFinalMigration = AuthTarget,
            Phases = inspection ? valid.Phases : new PhaseResults(new("rolled_back"), new("rolled_back"),
                new("failed"), new("untested")),
            AtomicAuth = valid.AtomicAuth! with
            {
                CommitAttempted = inspection, CommitAcknowledged = false, RollbackConfirmed = inspection ? null : true,
                DurableState = inspection ? "INCONCLUSIVE" : "ROLLED_BACK",
                FailedStage = inspection ? "COMMIT" : "FULL_V20_VERIFICATION"
            }
        };
    }

    private static MigrationEvidence CommitFailure(MigrationOperationContext context, bool current, string durable)
    {
        var valid = AtomicEvidence(context, current);
        return valid with
        {
            Classification = "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION",
            ResultCode = "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION",
            AtomicAuth = valid.AtomicAuth! with { CommitAcknowledged = false, DurableState = durable, FailedStage = "COMMIT" }
        };
    }

    private static MigrationEvidence PrecommitFailure(MigrationOperationContext context, string stage,
        string classification, string code, bool current = false, bool afterPhase = false)
    {
        var valid = AtomicEvidence(context, current);
        var migration = "not_started";
        var grants = "not_started";
        var manifest = "not_started";
        string? starting = stage == "HISTORY_AND_SCHEMA_RECHECK"
            && code is "ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED" or "ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED"
                ? current ? AuthTarget : AuthPredecessor : null;
        string? final = null;
        if (stage == "MIGRATION_SQL") { migration = "started"; starting = AuthPredecessor; }
        else if (stage == "APPROVED_GRANTS")
        {
            migration = "passed"; grants = afterPhase ? "passed" : "started"; starting = AuthPredecessor;
        }
        else if (stage is "EXACT_SCHEMA_VERIFICATION" or "FULL_V20_VERIFICATION")
        {
            migration = grants = current ? "not_needed" : "passed";
            starting = current ? AuthTarget : AuthPredecessor;
            if (stage == "FULL_V20_VERIFICATION") { manifest = afterPhase ? "passed" : "failed"; final = AuthTarget; }
        }
        var rolledBack = classification == "ATOMIC_AUTH_ROLLED_BACK";
        if (rolledBack)
        {
            if (migration is "started" or "passed") migration = "rolled_back";
            if (grants is "started" or "passed") grants = "rolled_back";
        }
        var observedRoles = stage == "ROLE_IDENTITY"
            ? code == "ATOMIC_AUTH_IDENTITY_REJECTED"
                ? new RoleAssertions("zeka_auth_migrator", "zeka_other_migrator", "zeka_auth_owner", "zeka_other_migrator", true, false, false)
                : afterPhase ? SuccessfulRoles("auth")
                    : new RoleAssertions("zeka_auth_migrator", "zeka_auth_migrator", "zeka_auth_owner", "zeka_auth_migrator", true, true, false)
            : stage is "HISTORY_AND_SCHEMA_RECHECK" or "MIGRATION_SQL" or "APPROVED_GRANTS"
                or "EXACT_SCHEMA_VERIFICATION" or "FULL_V20_VERIFICATION"
                ? SuccessfulRoles("auth")
                : new RoleAssertions("zeka_auth_migrator", null, "zeka_auth_owner", null, false, false, false);
        return valid with
        {
            Classification = classification, ResultCode = code, StartingMigration = starting, ObservedFinalMigration = final,
            AdvisoryLockAcquired = (stage is "ROLE_IDENTITY" or "HISTORY_AND_SCHEMA_RECHECK" or "MIGRATION_SQL"
                or "APPROVED_GRANTS" or "EXACT_SCHEMA_VERIFICATION" or "FULL_V20_VERIFICATION")
                || (stage == "ADVISORY_TRANSACTION_LOCK" && afterPhase),
            Roles = observedRoles,
            Phases = new PhaseResults(new(migration), new(grants), new(manifest), new("untested")),
            AtomicAuth = valid.AtomicAuth! with
            {
                CommitAttempted = false, CommitAcknowledged = false,
                RollbackConfirmed = classification == "REJECTED" ? null : rolledBack,
                DurableState = rolledBack ? "ROLLED_BACK" : "NOT_CHECKED",
                DatabaseName = stage == "ARTIFACT_BINDING" ? null : FixtureDatabase,
                BackendPid = stage == "ARTIFACT_BINDING" || (stage == "CONNECTION" && code == "ATOMIC_AUTH_FAILED") ? null : 42,
                FailedStage = stage
            }
        };
    }

    private static RoleAssertions SuccessfulRoles(string service) =>
        new("zeka_" + service + "_migrator", "zeka_" + service + "_migrator",
            "zeka_" + service + "_owner", "zeka_" + service + "_owner", true, true, true);

    private static string LogicalDatabase(string service) => service switch
    {
        "auth" => "AuthManagement", "adminarea" => "AdminAreaManagement", "client" => "ClientManagement",
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    private static MigrationOperationContext Context(string operation = "plan", string service = "auth",
        string requestedTarget = "latest", string? expectedDatabase = null)
    {
        if (OperatingSystem.IsLinux()) return ManagedMigrationAdapter.NewOperation(operation, service, SourceCommit, requestedTarget, expectedDatabase);
        return new MigrationOperationContext(Guid.NewGuid(), operation, service, SourceCommit, NewRoot(), requestedTarget, expectedDatabase);
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
            true, exit, null, new(false, 0, null, false, true), new(false, 0, null, false, true),
            context.RequestedTarget, context.ExpectedDatabase);

    private static MigrationEvidence Evidence(MigrationOperationContext context, string code = "SUCCESS",
        string classification = "FORWARD_APPROVED", string manifest = "not_started") =>
        new("zeka-db-migrate-result/v0.1.0", "0.1.0", context.SourceCommit, context.OperationId, context.Operation,
            context.Service, LogicalDatabase(context.Service), context.RequestedTarget, null,
            code == "CREDENTIAL_REJECTED" ? null : context.RequestedTarget == "latest" ? AuthTarget : context.RequestedTarget,
            null, classification, code, code != "CREDENTIAL_REJECTED",
            code != "CREDENTIAL_REJECTED" ? SuccessfulRoles(context.Service)
                : new RoleAssertions("zeka_" + context.Service + "_migrator", null, "zeka_" + context.Service + "_owner", null, false, false, false),
            new PhaseResults(new("not_started"), new("not_started"), new(manifest), new("untested")),
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            code == "CREDENTIAL_REJECTED" ? ["Bootstrap reconciliation and runtime readiness are separate host phases."] : []);
}
