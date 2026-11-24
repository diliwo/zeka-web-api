extern alias DbMigrator;
using System.Text;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using DotNet.Testcontainers.Containers;
using Xunit;
using Xunit.Abstractions;
using Zeka.LocalStack.DatabaseSetup;
using SafePostgresDiagnostic = DbMigrator::Zeka.ToolingDiagnostics.SafePostgresDiagnostic;
using SafePostgresText = DbMigrator::Zeka.ToolingDiagnostics.SafePostgresText;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-27")]
[Trait("Evidence", "ActualLinuxSetupEntryPoint")]
public sealed class LocalStackAuthInitializationTests(ITestOutputHelper output)
{
    // Owner-orchestrated witnesses are NOT_EXECUTED by the default CI build.
    // Explicitly build with -p:ZekaOperatorWitnesses=true, then run the exact
    // FullyQualifiedName through dotnet test --no-build --no-restore only after
    // admitting the required immutable images, source bindings and private inputs.
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public async Task Actual_Linux_setup_entrypoint_initializes_fresh_Auth_and_verifies_a_non_destructive_second_run()
#else
    internal async Task Actual_Linux_setup_entrypoint_initializes_fresh_Auth_and_verifies_a_non_destructive_second_run()
#endif
    {
        // Required execution inputs are admitted externally after source review. Missing
        // capability is a failure/NOT_EXECUTED proof, never a skip or fabricated success.
        var binding = AuthSetupImageBinding.Required();
        var fixture = new AuthOnboardingAtomicFixture();
        using (var admission = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            await fixture.AdmitSetupImageAsync(binding, admission.Token);
        Exception? primaryFailure = null;
        try
        {
            await fixture.InitializeAsync();
            await fixture.RunInitializationAsync(nameof(Actual_Linux_setup_entrypoint_initializes_fresh_Auth_and_verifies_a_non_destructive_second_run), output, async ct =>
            {
                var initialization = Guid.NewGuid().ToString("D");
                var request = new SetupRequest("zeka-m1", "auth", initialization, binding.SourceCommit,
                    false, null, null, null);
                var first = await InvokeAsync(fixture, binding, request, ct);
                Assert.Equal("INITIALIZED", first.Result.GetProperty("status").GetString());
                AssertResultIdentity(first.Result, binding, initialization);
                Assert.Equal(2, first.Result.GetProperty("bootstrapExecutions").GetInt32());
                AssertOperations(first, binding,
                [
                    ("plan", AuthOnboardingMigrationExecutor.FromMigration, "FORWARD_APPROVED"),
                    ("apply", AuthOnboardingMigrationExecutor.FromMigration, "FORWARD_APPROVED"),
                    ("plan", AuthOnboardingMigrationExecutor.TargetMigration, "FORWARD_APPROVED"),
                    ("apply", AuthOnboardingMigrationExecutor.TargetMigration, "ATOMIC_AUTH_APPLIED")
                ]);

                var database = fixture.InitializedAuthDatabase();
                // Program's atomic target already verified v20 before commit; independently
                // repeat it here before any second invocation or reconciliation could mask a defect.
                await database.VerifyV20Async(ct);
                await database.AssertCommittedAsync(ct);
                await database.AssertRuntimeDenialsAsync(ct);
                var before = await database.CaptureCatalogAsync(ct);
                var completed = request with
                {
                    Completed = true,
                    ClusterId = first.Result.GetProperty("clusterId").GetString(),
                    SchemaSha256 = first.Result.GetProperty("schemaSha256").GetString(),
                    HistorySha256 = first.Result.GetProperty("historySha256").GetString()
                };
                // A second helper receives the same saved fixture secrets in protected files.
                var second = await InvokeAsync(fixture, binding, completed, ct);
                Assert.Equal("ALREADY_CURRENT", second.Result.GetProperty("status").GetString());
                AssertResultIdentity(second.Result, binding, initialization);
                Assert.Equal(0, second.Result.GetProperty("bootstrapExecutions").GetInt32());
                AssertOperations(second, binding,
                [
                    ("plan", AuthOnboardingMigrationExecutor.TargetMigration, "ALREADY_CURRENT"),
                    ("apply", AuthOnboardingMigrationExecutor.TargetMigration, "ATOMIC_AUTH_ALREADY_CURRENT")
                ]);
                Assert.Equal(completed.ClusterId, second.Result.GetProperty("clusterId").GetString());
                Assert.Equal(completed.SchemaSha256, second.Result.GetProperty("schemaSha256").GetString());
                Assert.Equal(completed.HistorySha256, second.Result.GetProperty("historySha256").GetString());
                Assert.Empty(first.TerminalIds.Intersect(second.TerminalIds));
                await database.VerifyV20Async(ct);
                await database.AssertCommittedAsync(ct);
                await database.AssertRuntimeDenialsAsync(ct);
                var after = await database.CaptureCatalogAsync(ct);
                Assert.Equal(before.QuerySha256, after.QuerySha256);
                Assert.Equal(before.Rows, after.Rows);
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    actualLinuxProgram = true, sourceCommit = binding.SourceCommit,
                    sourceGraph = binding.SourceGraphSha256, image = binding.Image,
                    firstHelperId = first.HelperId, secondHelperId = second.HelperId,
                    firstOperations = first.TerminalIds, secondOperations = second.TerminalIds,
                    firstBootstrapExecutions = 2, secondBootstrapExecutions = 0,
                    fullCatalogSha256 = after.Sha256, rawHistoryProductVersionPreserved = true,
                    privateFiles = "uid1654:0400", credentialDelivery = "existing-private-files-and-native-stdin",
                    linuxPublisher = "unchanged-descriptor-relative-writer-on-private-tmpfs",
                    powerLossDurabilityProven = false, ownedResources = "stop-and-retain-no-reaper-claim"
                }));
            });
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { await fixture.DisposeAsync(); }
            catch (Exception cleanup) when (primaryFailure is not null)
            {
                // Neither a primary failure nor a failed cleanup may disappear.
                throw new AggregateException("OWNED_SETUP_PRIMARY_AND_CLEANUP_FAILURE", primaryFailure, cleanup);
            }
        }
    }

    // This explicit Windows-owner witness uses the same opt-in build above.
    // Its platform and input guards remain failures; default CI is NOT_EXECUTED.
    [Trait("Evidence", "LinuxAuthEf8ReferenceTransport")]
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public async Task Original_Auth_EF8_producer_uses_database_only_Linux_transport_and_final_success_admission()
#else
    internal async Task Original_Auth_EF8_producer_uses_database_only_Linux_transport_and_final_success_admission()
#endif
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("AUTH_CATALOG_WINDOWS_HOST_REQUIRED");
        var binding = AuthCatalogRunnerBinding.Required();
        var postgresBinding = AuthCatalogPostgresBinding.Required();
        var fixture = new AuthOnboardingAtomicFixture { AuthCatalogTransportRequested = true };
        var evidence = AuthCatalogPrivateEvidence.OpenRequired(fixture.CatalogRunId);
        Exception? primary = null;
        bool producerVerified = false, cleanupVerified = false;
        try
        {
            using (var admission = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                await fixture.AdmitAuthCatalogImageAsync(binding, admission.Token);
                await fixture.AdmitAuthCatalogPostgresImageAsync(postgresBinding, admission.Token);
            }
            await fixture.InitializeAsync();
            await fixture.RunInitializationAsync(nameof(Original_Auth_EF8_producer_uses_database_only_Linux_transport_and_final_success_admission), output, async ct =>
            {
                var helper = await fixture.StartAuthCatalogRunnerAsync(binding, ct);
                long? nativeExit = null;
                Exception? nativeFailure = null;
                string stdout = string.Empty, stderr = string.Empty;
                try
                {
                    // Only fixed paths/FQN enter the command. No shell interpolation of
                    // configuration, credentials, output paths or caller-controlled text.
                    var result = await helper.ExecAsync(["/bin/sh", "-c",
                        "umask 077; exec dotnet vstest /app/tests/Infrastructure.IntegrationTests.dll /TestAdapterPath:/app/tests /TestCaseFilter:FullyQualifiedName=Infrastructure.IntegrationTests.PostgreSqlAuthRoleCatalogEvidenceTests.Fresh_database_migrates_through_owner_assumption_and_matches_auth_manifest '/Logger:trx;LogFileName=producer.trx' /ResultsDirectory:/run/results/auth-ef8-" + fixture.CatalogRunId], ct);
                    nativeExit = result.ExitCode;
                    // Approve BOTH streams before retaining either. A rejected bare
                    // fixture secret must never reach the generic text protector.
                    var approved = AuthCatalogProducerGate.ApproveOutput(result.Stdout, result.Stderr,
                        fixture.AssertNoCredentialMaterial);
                    stdout = approved.Stdout;
                    stderr = approved.Stderr;
                }
                catch (Exception error) { nativeFailure = error; }
                // Capture even after cancellation/nonzero exit, before owned tmpfs is
                // stopped. A captured snapshot remains an untrusted candidate on failure.
                byte[]? snapshot = null, trx = null;
                var captureFailures = new List<object>();
                using (var capture = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    foreach (var file in new[] { "reference.json", "producer.trx" })
                    {
                        try
                        {
                            var bytes = await fixture.ReadAuthCatalogEvidenceAsync(helper, file, capture.Token);
                            evidence.WriteNew(file == "reference.json" ? "candidate-reference.json" : "producer.trx", bytes);
                            if (file == "reference.json") snapshot = bytes; else trx = bytes;
                        }
                        catch (Exception error) { captureFailures.Add(new { file, category = error.GetType().Name,
                            message = SafePostgresText.Protect(error.Message) }); }
                    }
                }
                string? safeFailure = null;
                if (nativeFailure is not null)
                {
                    try { fixture.AssertNoCredentialMaterial(nativeFailure.Message);
                        safeFailure = SafePostgresText.Protect(nativeFailure.Message); }
                    catch { safeFailure = "[NATIVE_FAILURE_MESSAGE_WITHHELD]"; }
                }
                evidence.WriteJson("native-result.json", new
                {
                    sourceCommit = binding.SourceCommit, sourceTree = binding.SourceTree,
                    image = binding.Image, fixtureId = fixture.CatalogRunId, helperId = helper.Id,
                    requestedPostgresImage = postgresBinding.Image, trustedPostgresRepoDigest = postgresBinding.RepoDigest,
                    observedPostgresImage = fixture.CatalogPostgresImage,
                    nativeCompleted = nativeExit is not null, nativeExit,
                    outputSecurityApproved = nativeFailure is null,
                    failureCategory = nativeFailure?.GetType().Name,
                    failureMessage = safeFailure,
                    stdout = SafePostgresText.Protect(stdout), stderr = SafePostgresText.Protect(stderr),
                    snapshotCaptured = snapshot is not null, trxCaptured = trx is not null,
                    captureFailures, rawFilesArePrivate = true, snapshotAdmitted = false
                });
                var producerTrx = AuthCatalogProducerGate.RequireFinalSuccess(nativeExit, nativeFailure is null, snapshot is not null, trx);
                AuthCatalogProducerGate.RequireRuntimeProvenance(producerTrx, binding, fixture.CatalogRunId);
                AuthCatalogProducerGate.RequireSnapshotIdentity(snapshot!, binding);
                await fixture.VerifyAuthCatalogInputsAsync(helper, ct);
                producerVerified = true;
            });
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            Exception? cleanupFailure = null;
            try { await fixture.DisposeAsync(); cleanupVerified = true; }
            catch (Exception error) { cleanupFailure = error; }
            try
            {
                evidence.WriteJson("completion.json", new { producerVerified, cleanupVerified,
                    admitted = false, promotionPending = producerVerified && cleanupVerified && primary is null,
                    sourceTree = binding.SourceTree,
                    requestedPostgresImage = postgresBinding.Image, trustedPostgresRepoDigest = postgresBinding.RepoDigest,
                    observedPostgresImage = fixture.CatalogPostgresImage,
                    runId = fixture.CatalogRunId });
            }
            catch (Exception error)
            { cleanupFailure = cleanupFailure is null ? error : new AggregateException(cleanupFailure, error); }
            if (cleanupFailure is not null)
                throw primary is null ? cleanupFailure
                    : new AggregateException("AUTH_CATALOG_PRIMARY_AND_CLEANUP_FAILURE", primary, cleanupFailure);
        }
        AuthCatalogProducerGate.RequireCleanup(producerVerified, cleanupVerified);
        evidence.AdmitReference();
        evidence.WriteJson("admission.json", new { admitted = true, producerVerified, cleanupVerified,
            sourceCommit = binding.SourceCommit, sourceTree = binding.SourceTree, runId = fixture.CatalogRunId,
            requestedPostgresImage = postgresBinding.Image, trustedPostgresRepoDigest = postgresBinding.RepoDigest,
            observedPostgresImage = fixture.CatalogPostgresImage });
        output.WriteLine(JsonSerializer.Serialize(new { sourceCommit = binding.SourceCommit,
            sourceTree = binding.SourceTree, image = binding.Image, fixtureId = fixture.CatalogRunId,
            requestedPostgresImage = postgresBinding.Image, trustedPostgresRepoDigest = postgresBinding.RepoDigest,
            observedPostgresImage = fixture.CatalogPostgresImage,
            producer = AuthCatalogProducerGate.Fqn, nativeExit = 0, passed = 1, skipped = 0,
            snapshotAdmitted = true, linux = true, entityFramework = "8.0.8", npgsql = "8.0.4",
            evidence = "owner-private-source-bound-run-directory", crossEfComparisonExecuted = false }));
    }

    private static void AssertResultIdentity(JsonElement result, AuthSetupImageBinding binding, string initialization)
    {
        Assert.Equal("auth", result.GetProperty("service").GetString());
        Assert.Equal("zeka_m1_auth", result.GetProperty("database").GetString());
        Assert.Equal(binding.SourceCommit, result.GetProperty("sourceCommit").GetString());
        Assert.Equal(initialization, result.GetProperty("initializationId").GetString());
        Assert.Equal(AuthOnboardingMigrationExecutor.TargetMigration, result.GetProperty("finalMigration").GetString());
        Assert.Equal("passed", result.GetProperty("manifest").GetString());
        Assert.Equal("passed", result.GetProperty("runtimeAuthentication").GetString());
        Assert.True(result.GetProperty("wrongPasswordRejected").GetBoolean());
        Assert.Equal("zeka_auth_runtime", result.GetProperty("runtimeIdentity").GetString());
        Assert.Equal("zeka_auth_migrator", result.GetProperty("migratorIdentity").GetString());
    }

    private async Task<SetupCliObservation> InvokeAsync(AuthOnboardingAtomicFixture fixture,
        AuthSetupImageBinding binding, SetupRequest request, CancellationToken ct)
    {
        IContainer helper = await fixture.StartSetupHelperAsync(binding, request, ct);
        var process = await helper.ExecAsync(["dotnet", "/app/zeka-local-db-setup.dll"], ct);
        output.WriteLine(JsonSerializer.Serialize(new { diagnostic = "setup-native-exit", helperId = helper.Id,
            exit = process.ExitCode, stdoutPresent = !string.IsNullOrEmpty(process.Stdout),
            stderrPresent = !string.IsNullOrEmpty(process.Stderr) }));
        fixture.AssertNoCredentialMaterial(process.Stdout);
        fixture.AssertNoCredentialMaterial(process.Stderr);
        if (!string.IsNullOrWhiteSpace(process.Stdout))
        {
            try
            {
                using var terminal = JsonDocument.Parse(process.Stdout);
                WritePublicDiagnostic(fixture, helper.Id, "stdout", terminal.RootElement);
            }
            catch (JsonException)
            {
                output.WriteLine(JsonSerializer.Serialize(new { diagnostic = "setup-public-stdout-not-json",
                    helperId = helper.Id, exit = process.ExitCode }));
            }
        }
        await fixture.VerifySetupInputsAsync(helper, ct);
        Assert.Equal(0, process.ExitCode);
        var resultBytes = await fixture.ReadSetupEvidenceAsync(helper, "/run/results/result.json", ct);
        fixture.AssertNoCredentialMaterial(Encoding.UTF8.GetString(resultBytes));
        using var result = JsonDocument.Parse(resultBytes);
        WritePublicDiagnostic(fixture, helper.Id, "result-file", result.RootElement);
        var listed = await helper.ExecAsync(["find", "/run/results", "-maxdepth", "1", "-type", "f", "-name", "*.json", "-printf", "%f\n"], ct);
        Assert.Equal(0, listed.ExitCode);
        var files = listed.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var terminals = new Dictionary<Guid, JsonElement>();
        var natives = new Dictionary<Guid, JsonElement>();
        foreach (var file in files.Where(file => file != "result.json"))
        {
            Assert.True(file.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.'));
            var bytes = await fixture.ReadSetupEvidenceAsync(helper, "/run/results/" + file, ct);
            fixture.AssertNoCredentialMaterial(Encoding.UTF8.GetString(bytes));
            using var document = JsonDocument.Parse(bytes);
            if (file.StartsWith("native-", StringComparison.Ordinal))
            {
                var native = document.RootElement;
                var operation = native.GetProperty("OperationId").GetGuid();
                Assert.True(natives.TryAdd(operation, native.Clone()));
                var action = native.GetProperty("Operation").GetString();
                Assert.True(action is "plan" or "apply");
                var privateRoot = "/tmp/zeka-m1-migration-" + operation.ToString("N");
                Assert.Equal(privateRoot, native.GetProperty("PrivateEvidenceRoot").GetString());
                var terminalFile = action + "-" + operation.ToString("D") + ".json";
                var original = await fixture.ReadSetupEvidenceAsync(helper, privateRoot + "/" + terminalFile, ct);
                var exported = await fixture.ReadSetupEvidenceAsync(helper, "/run/results/" + terminalFile, ct);
                Assert.True(original.AsSpan().SequenceEqual(exported), "CLI terminal export must preserve the private publisher bytes.");
                var modes = await helper.ExecAsync(["stat", "-c", "%u:%g:%a", privateRoot, privateRoot + "/" + terminalFile], ct);
                Assert.Equal(0, modes.ExitCode);
                Assert.Equal(new[] { "1654:1654:700", "1654:1654:600" },
                    modes.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
            else
            {
                var terminal = document.RootElement;
                Assert.True(terminals.TryAdd(terminal.GetProperty("operationId").GetGuid(), terminal.Clone()));
            }
        }
        return new SetupCliObservation(helper.Id, result.RootElement.Clone(), terminals, natives);
    }

    private void WritePublicDiagnostic(AuthOnboardingAtomicFixture fixture, string helperId, string origin, JsonElement result)
    {
        fixture.AssertNoCredentialMaterial(result.GetRawText());
        var fields = new Dictionary<string, object?>();
        foreach (var name in new[] { "status", "stage", "code", "sqlState", "nativeExit", "operationId", "receiptFile",
            "exceptionType", "bootstrapExecutions", "service", "manifest", "runtime", "negativePassword",
            "planExit", "planClassification", "applyExit", "applyClassification", "finalMigration" })
        {
            if (!result.TryGetProperty(name, out var value)) continue;
            fields[name] = value.ValueKind switch
            {
                JsonValueKind.String => SafePostgresText.Protect(value.GetString()!),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => value.Clone(),
                _ => throw new InvalidOperationException("SETUP_PUBLIC_DIAGNOSTIC_REJECTED")
            };
        }
        if (result.TryGetProperty("postgresDiagnostic", out var diagnostic))
            fields["postgresDiagnostic"] = diagnostic.ValueKind == JsonValueKind.Null ? null : SafePostgresDiagnostic.Parse(diagnostic);
        output.WriteLine(JsonSerializer.Serialize(new { diagnostic = "setup-public-result", helperId, origin, fields }));
    }

    [Theory]
    [InlineData("/run/results/result.json", true)]
    [InlineData("/run/results/native-plan-00000000-0000-0000-0000-000000000001.json", true)]
    [InlineData("/tmp/zeka-m1-migration-00000000000000000000000000000001/plan-00000000-0000-0000-0000-000000000001.json", true)]
    [InlineData("/tmp/zeka-m1-migration-00000000000000000000000000000001/apply-00000000-0000-0000-0000-000000000001.json", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("/run/secrets/admin-password", false)]
    [InlineData("/run/secrets/runtime-config.json", false)]
    [InlineData("/run/results/../secrets/runtime-config.json", false)]
    [InlineData("/run/results//result.json", false)]
    [InlineData("/run/results/nested/result.json", false)]
    [InlineData("/run/results-not-admitted/result.json", false)]
    [InlineData("/run/results/result.txt", false)]
    [InlineData("/run/results/.json", false)]
    [InlineData("/run/results/result.json\n", false)]
    [InlineData("/tmp/zeka-m1-migration-00000000000000000000000000000000/plan-00000000-0000-0000-0000-000000000000.json", false)]
    [InlineData("/tmp/zeka-m1-migration-00000000000000000000000000000001/plan-00000000-0000-0000-0000-000000000002.json", false)]
    [InlineData("/tmp/zeka-m1-migration-00000000000000000000000000000001/../result.json", false)]
    [InlineData("/tmp/not-owned/result.json", false)]
    [InlineData("C:/run/results/result.json", false)]
    public void Setup_evidence_reader_path_boundary_rejects_private_inputs_and_aliases(string? path, bool allowed)
        => Assert.Equal(allowed, AuthOnboardingAtomicFixture.IsSetupEvidencePath(path));

    private static void AssertOperations(SetupCliObservation observed, AuthSetupImageBinding binding,
        (string Operation, string Target, string Classification)[] expected)
    {
        var operations = observed.Result.GetProperty("migrationOperations").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, operations.Length);
        Assert.Equal(expected.Length, observed.Terminals.Count);
        Assert.Equal(expected.Length, observed.Natives.Count);
        Assert.Equal(observed.TerminalIds, observed.Natives.Keys.OrderBy(id => id).ToArray());
        for (var index = 0; index < expected.Length; index++)
        {
            var recorded = operations[index];
            var id = recorded.GetProperty("operationId").GetGuid();
            Assert.True(observed.Terminals.ContainsKey(id));
            var terminal = observed.Terminals[id];
            var native = observed.Natives[id];
            var contract = expected[index];
            Assert.Equal(contract.Operation, recorded.GetProperty("operation").GetString());
            Assert.Equal(contract.Target, recorded.GetProperty("requestedTarget").GetString());
            Assert.Equal(contract.Classification, recorded.GetProperty("classification").GetString());
            Assert.Equal(0, recorded.GetProperty("exit").GetInt32());
            Assert.Equal(binding.SourceCommit, terminal.GetProperty("sourceCommit").GetString());
            Assert.Equal(contract.Operation, terminal.GetProperty("operation").GetString());
            Assert.Equal(contract.Target, terminal.GetProperty("requestedTarget").GetString());
            Assert.Equal(contract.Classification, terminal.GetProperty("classification").GetString());
            Assert.Equal("SUCCESS", terminal.GetProperty("resultCode").GetString());
            Assert.Equal("auth", terminal.GetProperty("service").GetString());
            Assert.Equal("AuthManagement", terminal.GetProperty("logicalDatabase").GetString());
            Assert.True(terminal.GetProperty("advisoryLockAcquired").GetBoolean());
            Assert.Equal(id, native.GetProperty("OperationId").GetGuid());
            Assert.Equal(contract.Operation, native.GetProperty("Operation").GetString());
            Assert.Equal(contract.Target, native.GetProperty("RequestedTarget").GetString());
            Assert.Equal(binding.SourceCommit, native.GetProperty("SourceCommit").GetString());
            Assert.True(native.GetProperty("NativeStarted").GetBoolean());
            Assert.Equal(0, native.GetProperty("NativeExit").GetInt32());
            Assert.Equal(JsonValueKind.Null, native.GetProperty("ExecutionFailure").ValueKind);
            Assert.True(native.GetProperty("StandardOutput").GetProperty("Complete").GetBoolean());
            Assert.True(native.GetProperty("StandardError").GetProperty("Complete").GetBoolean());
            var roles = terminal.GetProperty("roles");
            Assert.Equal("zeka_auth_migrator", roles.GetProperty("observedSessionUser").GetString());
            Assert.Equal("zeka_auth_owner", roles.GetProperty("observedCurrentUser").GetString());
            Assert.True(roles.GetProperty("restrictedAttributes").GetBoolean());
            Assert.True(roles.GetProperty("matchingOwnerMembership").GetBoolean());
            Assert.True(roles.GetProperty("ownerAssumption").GetBoolean());
            if (contract.Operation == "apply" && contract.Target == AuthOnboardingMigrationExecutor.TargetMigration)
            {
                var atomic = terminal.GetProperty("atomicAuth");
                Assert.Equal(AuthOnboardingSqlArtifacts.MigrationSha256, atomic.GetProperty("migrationSqlSha256").GetString());
                Assert.Equal(AuthOnboardingSqlArtifacts.GrantSha256, atomic.GetProperty("grantSqlSha256").GetString());
                Assert.Equal(AuthOnboardingSqlArtifacts.ManifestSha256, atomic.GetProperty("manifestSha256").GetString());
                Assert.Equal("zeka_m1_auth", atomic.GetProperty("databaseName").GetString());
                Assert.True(atomic.GetProperty("backendPid").GetInt32() > 0);
                Assert.True(atomic.GetProperty("commitAttempted").GetBoolean());
                Assert.True(atomic.GetProperty("commitAcknowledged").GetBoolean());
                Assert.False(atomic.GetProperty("replayAttempted").GetBoolean());
                Assert.Equal("COMMITTED_VERIFIED", atomic.GetProperty("durableState").GetString());
                Assert.Equal(JsonValueKind.Null, atomic.GetProperty("failedStage").ValueKind);
                Assert.Equal("passed", terminal.GetProperty("phases").GetProperty("manifestVerification").GetProperty("status").GetString());
            }
            else Assert.False(terminal.TryGetProperty("atomicAuth", out _));
        }
    }

    private sealed record SetupCliObservation(string HelperId, JsonElement Result, Dictionary<Guid, JsonElement> Terminals,
        Dictionary<Guid, JsonElement> Natives)
    {
        internal Guid[] TerminalIds => Terminals.Keys.OrderBy(id => id).ToArray();
    }
}

internal sealed record AuthSetupImageBinding(string Image, string SourceCommit, string SourceGraphSha256)
{
    internal static AuthSetupImageBinding Required()
    {
        var image = Environment.GetEnvironmentVariable("ZEKA_M1_SETUP_TEST_IMAGE");
        var source = Environment.GetEnvironmentVariable("ZEKA_M1_SETUP_TEST_SOURCE_COMMIT");
        var graph = Environment.GetEnvironmentVariable("ZEKA_M1_SETUP_TEST_SOURCE_GRAPH_SHA256");
        if (image is null || !image.StartsWith("sha256:", StringComparison.Ordinal) || !LowerHex(image[7..], 64)
            || !LowerHex(source, 40) || !LowerHex(graph, 64))
            throw new InvalidOperationException("ADMITTED_IMMUTABLE_SETUP_CLI_IMAGE_AND_SOURCE_BINDINGS_REQUIRED");
        return new(image, source!, graph!);
    }

    private static bool LowerHex(string? value, int length) => value?.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// These are separately admitted owner inputs. A local inspect checks their exact
// correspondence; it does not independently establish the supplied digest's trust.
internal sealed record AuthCatalogPostgresBinding(string Image, string RepoDigest)
{
    internal static AuthCatalogPostgresBinding Required() => FromRequiredInputs(
        Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_POSTGRES_IMAGE"),
        Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_POSTGRES_REPO_DIGEST"));

    internal static AuthCatalogPostgresBinding FromRequiredInputs(string? image, string? repoDigest)
    {
        const string repository = "postgres@sha256:";
        AuthCatalogRunnerBinding.Require(image is not null && image.StartsWith("sha256:", StringComparison.Ordinal)
            && AuthCatalogRunnerBinding.LowerHex(image[7..], 64)
            && repoDigest is not null && repoDigest.StartsWith(repository, StringComparison.Ordinal)
            && AuthCatalogRunnerBinding.LowerHex(repoDigest[repository.Length..], 64),
            "AUTH_CATALOG_EXPLICIT_TRUSTED_POSTGRES_BINDING_REQUIRED");
        return new(image!, repoDigest!);
    }

    internal void RequireLocalImage(JsonElement image)
    {
        FromRequiredInputs(Image, RepoDigest);
        AuthCatalogRunnerBinding.Require(image.ValueKind == JsonValueKind.Object,
            "AUTH_CATALOG_POSTGRES_IMAGE_METADATA_REJECTED");
        AuthCatalogRunnerBinding.RequireUnique(image);
        AuthCatalogRunnerBinding.Require(image.EnumerateObject().Count() == 4
            && image.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == Image
            && image.TryGetProperty("os", out var os) && os.ValueKind == JsonValueKind.String && os.GetString() == "linux"
            && image.TryGetProperty("architecture", out var architecture)
            && architecture.ValueKind == JsonValueKind.String && architecture.GetString() == "amd64"
            && image.TryGetProperty("repoDigests", out var digests) && digests.ValueKind == JsonValueKind.Array
            && digests.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String)
            && digests.EnumerateArray().Any(value => value.GetString() == RepoDigest),
            "AUTH_CATALOG_EXACT_POSTGRES_IMAGE_PROVENANCE_REQUIRED");
    }

    internal string RequireCreatedImage(string? actualImage)
    {
        FromRequiredInputs(Image, RepoDigest);
        AuthCatalogRunnerBinding.Require(actualImage == Image, "AUTH_CATALOG_CREATED_POSTGRES_IMAGE_REJECTED");
        return actualImage!;
    }
}

// This descriptor is produced only from a separately built/scanned portable Auth
// closure after source admission. It is not a package/restore or execution bypass.
internal sealed record AuthCatalogRunnerBinding(string Image, string SourceCommit, string SourceTree,
    string PayloadRoot, IReadOnlyDictionary<string, string> PayloadHashes,
    string TestAssemblySha256, string PayloadManifestSha256, string SdkVersion)
{
    internal static AuthCatalogRunnerBinding Required()
    {
        var image = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_RUNNER_IMAGE");
        var source = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_SOURCE_COMMIT");
        var tree = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_SOURCE_TREE");
        var payload = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_TEST_OUTPUT_DIR");
        var descriptor = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_TEST_OUTPUT_MANIFEST");
        Require(image is not null && image.StartsWith("sha256:", StringComparison.Ordinal)
            && LowerHex(image[7..], 64) && LowerHex(source, 40) && LowerHex(tree, 40)
            && source == "29a299f770481fe3580be18a0fbdba3043ecc884"
            && payload is not null && descriptor is not null, "AUTH_CATALOG_EXPLICIT_BINDINGS_REQUIRED");
        ValidateLocalPath(payload!, true);
        ValidateLocalPath(descriptor!, false);
        Require(new FileInfo(descriptor!).Length is > 0 and <= 2 * 1024 * 1024,
            "AUTH_CATALOG_MANIFEST_SIZE_REJECTED");
        var raw = File.ReadAllBytes(descriptor!);
        using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 16 });
        RequireUnique(document.RootElement);
        var root = document.RootElement;
        Require(root.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
            .SequenceEqual(new[] { "entityFrameworkVersion", "files", "npgsqlVersion", "schemaVersion", "sdkVersion",
                "sourceCommit", "sourceTree", "testAssemblySha256" }), "AUTH_CATALOG_MANIFEST_FIELDS_REJECTED");
        Require(root.GetProperty("schemaVersion").GetInt32() == 1
            && root.GetProperty("sourceCommit").GetString() == source
            && root.GetProperty("sourceTree").GetString() == tree
            && IsStableNet8Sdk(root.GetProperty("sdkVersion").GetString())
            && root.GetProperty("entityFrameworkVersion").GetString() == "8.0.8"
            && root.GetProperty("npgsqlVersion").GetString() == "8.0.4",
            "AUTH_CATALOG_MANIFEST_BINDINGS_REJECTED");
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var caseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = root.GetProperty("files").EnumerateArray().ToArray();
        Require(entries.Length is > 10 and <= 512, "AUTH_CATALOG_PAYLOAD_COUNT_REJECTED");
        foreach (var item in entries)
        {
            Require(item.EnumerateObject().Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(new[] { "path", "sha256" }), "AUTH_CATALOG_PAYLOAD_FIELDS_REJECTED");
            var path = item.GetProperty("path").GetString();
            var hash = item.GetProperty("sha256").GetString();
            Require(AuthOnboardingAtomicFixture.IsAuthCatalogPayloadPath(path) && LowerHex(hash, 64)
                && caseNames.Add(path!) && hashes.TryAdd(path!, hash!), "AUTH_CATALOG_PAYLOAD_IDENTITY_REJECTED");
            Require(PortablePayloadPath(path!), "AUTH_CATALOG_NONPORTABLE_PAYLOAD_REJECTED");
            var file = Path.Combine(payload!, path!.Replace('/', Path.DirectorySeparatorChar));
            ValidateLocalPath(file, false);
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant() == hash,
                "AUTH_CATALOG_PAYLOAD_HASH_REJECTED");
            if (path.EndsWith(".dll", StringComparison.Ordinal))
            {
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                Require(pe.HasMetadata, "AUTH_CATALOG_WINDOWS_NATIVE_DLL_REJECTED");
            }
        }
        var found = new List<string>();
        var directories = new Stack<string>(); directories.Push(payload!);
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            ValidateLocalPath(directory, true);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                var isDirectory = Directory.Exists(child);
                ValidateLocalPath(child, isDirectory);
                if (isDirectory) directories.Push(child);
                else found.Add(Path.GetRelativePath(payload!, child).Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        var actual = found.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        Require(actual.SequenceEqual(hashes.Keys.OrderBy(path => path, StringComparer.Ordinal)),
            "AUTH_CATALOG_UNMANIFESTED_PAYLOAD_REJECTED");
        var testHash = root.GetProperty("testAssemblySha256").GetString();
        Require(LowerHex(testHash, 64) && hashes.TryGetValue("tests/Infrastructure.IntegrationTests.dll", out var observed)
            && observed == testHash, "AUTH_CATALOG_TEST_ASSEMBLY_HASH_REJECTED");
        using var deps = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(payload!, "tests", "Infrastructure.IntegrationTests.deps.json")));
        var libraries = deps.RootElement.GetProperty("libraries");
        Require(libraries.TryGetProperty("Microsoft.EntityFrameworkCore/8.0.8", out _)
            && libraries.TryGetProperty("Npgsql/8.0.4", out _)
            && !libraries.EnumerateObject().Any(value => value.Name.StartsWith("Microsoft.EntityFrameworkCore/", StringComparison.Ordinal)
                && value.Name != "Microsoft.EntityFrameworkCore/8.0.8"), "AUTH_CATALOG_AUTH_DEPENDENCY_CLOSURE_REJECTED");
        return new(image!, source!, tree!, payload!, hashes, testHash!,
            Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), root.GetProperty("sdkVersion").GetString()!);
    }

    internal static bool IsStableNet8Sdk(string? value)
        => value is not null && value.StartsWith("8.0.", StringComparison.Ordinal)
            && value.Length is >= 7 and <= 9 && value[4..].All(char.IsAsciiDigit)
            && int.TryParse(value[4..], out var patch) && patch >= 100;

    internal static bool PortablePayloadPath(string path)
    {
        if (!AuthOnboardingAtomicFixture.IsAuthCatalogPayloadPath(path)) return false;
        if (path == "Deployments/database/bootstrap-auth-roles.sql") return true;
        if (!path.StartsWith("tests/", StringComparison.Ordinal)) return false;
        if (path.StartsWith("tests/runtimes/", StringComparison.Ordinal)
            && !path.StartsWith("tests/runtimes/linux-x64/", StringComparison.Ordinal)) return false;
        return path.EndsWith(".dll", StringComparison.Ordinal) || path.EndsWith(".pdb", StringComparison.Ordinal)
            || path.EndsWith(".deps.json", StringComparison.Ordinal) || path.EndsWith(".runtimeconfig.json", StringComparison.Ordinal)
            || path.StartsWith("tests/runtimes/linux-x64/", StringComparison.Ordinal)
                && (path.EndsWith(".so", StringComparison.Ordinal) || path.Contains(".so.", StringComparison.Ordinal));
    }

    internal static void ValidateLocalPath(string path, bool directory)
    {
        Require(Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path
            && (directory ? Directory.Exists(path) : File.Exists(path)), "AUTH_CATALOG_HOST_PATH_REJECTED");
        for (FileSystemInfo? value = directory ? new DirectoryInfo(path) : new FileInfo(path); value is not null;
            value = value is FileInfo file ? file.Directory : ((DirectoryInfo)value).Parent)
            Require((value.Attributes & FileAttributes.ReparsePoint) == 0 && value.LinkTarget is null,
                "AUTH_CATALOG_HOST_LINK_REJECTED");
    }

    internal static void RequireUnique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            { Require(names.Add(item.Name), "AUTH_CATALOG_DUPLICATE_JSON_PROPERTY"); RequireUnique(item.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUnique(item);
    }
    internal static bool LowerHex(string? value, int length) => value is not null && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void Require(bool value, string code)
    { if (!value) throw new InvalidOperationException(code); }
}

internal static class AuthCatalogProducerGate
{
    internal const string ProducerClass = "Infrastructure.IntegrationTests.PostgreSqlAuthRoleCatalogEvidenceTests";
    internal const string ProducerMethod = "Fresh_database_migrates_through_owner_assumption_and_matches_auth_manifest";
    internal const string Fqn = ProducerClass + "." + ProducerMethod;
    private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    internal static (string Stdout, string Stderr) ApproveOutput(string stdout, string stderr, Action<string> guard)
    {
        guard(stdout); guard(stderr);
        return (stdout, stderr);
    }

    internal static XDocument RequireFinalSuccess(long? nativeExit, bool nativeCompleted, bool snapshotPresent, byte[]? trx)
    {
        AuthCatalogRunnerBinding.Require(nativeCompleted && nativeExit == 0 && snapshotPresent && trx is not null,
            "AUTH_CATALOG_FINAL_NATIVE_SUCCESS_REQUIRED");
        using var input = new MemoryStream(trx!);
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024, MaxCharactersFromEntities = 0 });
        var document = XDocument.Load(reader);
        AuthCatalogRunnerBinding.Require(document.Root?.Name == Ns + "TestRun"
            && !document.Descendants(Ns + "ErrorInfo").Any(), "AUTH_CATALOG_TRX_ROOT_OR_ERROR_REJECTED");
        foreach (var pair in new[] { (Container: "TestDefinitions", Item: "UnitTest"),
            (Container: "Results", Item: "UnitTestResult"), (Container: "TestEntries", Item: "TestEntry") })
        {
            var containers = document.Root!.Elements(Ns + pair.Container).ToArray();
            AuthCatalogRunnerBinding.Require(containers.Length == 1
                && containers[0].Elements().All(value => value.Name == Ns + pair.Item),
                "AUTH_CATALOG_TRX_INVENTORY_REJECTED");
        }
        var definitions = document.Root!.Element(Ns + "TestDefinitions")!.Elements(Ns + "UnitTest").ToArray();
        var results = document.Root!.Element(Ns + "Results")!.Elements(Ns + "UnitTestResult").ToArray();
        var entries = document.Root!.Element(Ns + "TestEntries")!.Elements(Ns + "TestEntry").ToArray();
        AuthCatalogRunnerBinding.Require(definitions.Length == 1 && results.Length == 1 && entries.Length == 1,
            "AUTH_CATALOG_EXACT_ONE_PRODUCER_REQUIRED");
        var definition = definitions[0]; var result = results[0]; var entry = entries[0];
        var method = definition.Element(Ns + "TestMethod");
        var execution = definition.Element(Ns + "Execution");
        var testId = definition.Attribute("id")?.Value;
        var executionId = execution?.Attribute("id")?.Value;
        AuthCatalogRunnerBinding.Require(Guid.TryParseExact(testId, "D", out var parsedId) && parsedId != Guid.Empty
            && Guid.TryParseExact(executionId, "D", out var parsedExecution) && parsedExecution != Guid.Empty
            && testId == result.Attribute("testId")?.Value && testId == entry.Attribute("testId")?.Value
            && executionId == result.Attribute("executionId")?.Value && executionId == entry.Attribute("executionId")?.Value
            && method?.Attribute("className")?.Value == ProducerClass && method.Attribute("name")?.Value == ProducerMethod
            && method.Attribute("codeBase")?.Value == "/app/tests/Infrastructure.IntegrationTests.dll"
            && result.Attribute("outcome")?.Value == "Passed", "AUTH_CATALOG_PRODUCER_LINK_OR_IDENTITY_REJECTED");
        var summary = document.Root!.Elements(Ns + "ResultSummary").ToArray();
        AuthCatalogRunnerBinding.Require(summary.Length == 1 && summary[0].Attribute("outcome")?.Value == "Completed",
            "AUTH_CATALOG_TRX_SUMMARY_REJECTED");
        var counters = summary[0].Elements(Ns + "Counters").ToArray();
        AuthCatalogRunnerBinding.Require(counters.Length == 1, "AUTH_CATALOG_TRX_COUNTERS_REQUIRED");
        var required = new[] { "total", "executed", "passed", "failed", "error", "timeout", "aborted", "inconclusive",
            "passedButRunAborted", "notRunnable", "notExecuted", "disconnected", "warning", "completed", "inProgress", "pending" };
        AuthCatalogRunnerBinding.Require(counters[0].Attributes().Select(value => value.Name.LocalName)
            .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(required.OrderBy(value => value, StringComparer.Ordinal)),
            "AUTH_CATALOG_TRX_COUNTER_INVENTORY_REJECTED");
        foreach (var counter in required)
            AuthCatalogRunnerBinding.Require(counters[0].Attribute(counter)?.Value ==
                (counter is "total" or "executed" or "passed" ? "1" : "0"), "AUTH_CATALOG_TRX_COUNTER_MISMATCH");
        return document;
    }

    internal static void RequireRuntimeProvenance(XDocument trx, AuthCatalogRunnerBinding binding, string runId)
    {
        var records = new List<JsonElement>();
        foreach (var output in trx.Descendants(Ns + "UnitTestResult").Elements(Ns + "Output").Elements(Ns + "StdOut"))
        foreach (var line in output.Value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith("{", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(line);
            AuthCatalogRunnerBinding.RequireUnique(json.RootElement);
            if (json.RootElement.TryGetProperty("transport", out var transport) && transport.GetString() == "external-file")
                records.Add(json.RootElement.Clone());
        }
        AuthCatalogRunnerBinding.Require(records.Count == 1, "AUTH_CATALOG_RUNTIME_PROVENANCE_REQUIRED");
        var row = records[0];
        AuthCatalogRunnerBinding.Require(row.GetProperty("fixtureId").GetString() == runId
            && row.GetProperty("resourceOwner").GetString() == "windows-host"
            && row.GetProperty("host").GetString() == "postgres-" + runId
            && row.GetProperty("port").GetInt32() == 5432
            && row.GetProperty("sourceCommit").GetString() == binding.SourceCommit
            && row.GetProperty("sourceTree").GetString() == binding.SourceTree
            && row.GetProperty("defaultDurability").GetBoolean()
            && row.GetProperty("disposal").GetString() == "own-connections-only"
            && row.GetProperty("hostTargetAndNetworkAdmissionRequired").GetBoolean(),
            "AUTH_CATALOG_RUNTIME_TARGET_REJECTED");
        RequireAssembly(row.GetProperty("producerAssembly"), "Infrastructure.IntegrationTests", binding.TestAssemblySha256, null);
        RequireAssembly(row.GetProperty("entityFramework"), "Microsoft.EntityFrameworkCore",
            binding.PayloadHashes["tests/Microsoft.EntityFrameworkCore.dll"], "8.0.8");
        RequireAssembly(row.GetProperty("npgsql"), "Npgsql", binding.PayloadHashes["tests/Npgsql.dll"], "8.0.4");
    }

    internal static void RequireSnapshotIdentity(byte[] bytes, AuthCatalogRunnerBinding binding)
    {
        AuthCatalogRunnerBinding.Require(bytes.Length is > 0 and <= 8 * 1024 * 1024,
            "AUTH_CATALOG_STRICT_CONSUMER_SIZE_REJECTED");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        AuthCatalogRunnerBinding.RequireUnique(document.RootElement);
        AuthCatalogRunnerBinding.Require(document.RootElement.EnumerateObject().Count() == 7,
            "AUTH_CATALOG_SNAPSHOT_FIELD_INVENTORY_REJECTED");
        var reference = JsonSerializer.Deserialize<AuthCatalogSnapshot>(bytes, new JsonSerializerOptions
            { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 })
            ?? throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_REJECTED");
        AuthCatalogRunnerBinding.Require(reference.SchemaVersion == "zeka-auth-catalog/v1"
            && AuthCatalogRunnerBinding.LowerHex(reference.QuerySha256, 64)
            && reference.Rows.Length is > 0 and <= 20000
            && reference.Rows.All(row => row is not null && row.Length <= 65536)
            && reference.Rows.SequenceEqual(reference.Rows.OrderBy(row => row, StringComparer.Ordinal)),
            "AUTH_CATALOG_SNAPSHOT_SHAPE_REJECTED");
        RequireAssembly(document.RootElement.GetProperty("ProducerAssembly"), "Infrastructure.IntegrationTests",
            binding.TestAssemblySha256, null);
        RequireAssembly(document.RootElement.GetProperty("AuthInfrastructure"), "AuthManager.Infrastructure",
            binding.PayloadHashes["tests/AuthManager.Infrastructure.dll"], null);
        RequireAssembly(document.RootElement.GetProperty("EntityFramework"), "Microsoft.EntityFrameworkCore",
            binding.PayloadHashes["tests/Microsoft.EntityFrameworkCore.dll"], "8.0.8");
        // Exact query/history/security equivalence remains the unchanged consumer's
        // obligation. Never normalize or rewrite the producer bytes here.
    }
    private static void RequireAssembly(JsonElement row, string name, string hash, string? version)
    {
        AuthCatalogRunnerBinding.Require(row.GetProperty("Name").GetString() == name
            && row.GetProperty("Sha256").GetString() == hash
            && Guid.TryParseExact(row.GetProperty("ModuleVersionId").GetString(), "D", out var id) && id != Guid.Empty
            && (version is null || row.GetProperty("InformationalVersion").GetString()?.Split('+')[0] == version),
            "AUTH_CATALOG_LOADED_ASSEMBLY_REJECTED");
    }
    internal static void RequireCleanup(bool producer, bool cleanup)
        => AuthCatalogRunnerBinding.Require(producer && cleanup, "AUTH_CATALOG_CLEANUP_NOT_VERIFIED");
}

internal sealed class AuthCatalogPrivateEvidence
{
    private readonly string root;
    private AuthCatalogPrivateEvidence(string root) => this.root = root;
    internal static AuthCatalogPrivateEvidence OpenRequired(string runId)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("AUTH_CATALOG_WINDOWS_EVIDENCE_REQUIRED");
        var parent = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_PRIVATE_EVIDENCE_ROOT")
            ?? throw new InvalidOperationException("AUTH_CATALOG_PRIVATE_EVIDENCE_ROOT_REQUIRED");
        AuthCatalogRunnerBinding.ValidateLocalPath(parent, true);
        RequireOwnerAcl(parent, true);
        AuthCatalogRunnerBinding.Require(AuthCatalogRunnerBinding.LowerHex(runId, 32), "AUTH_CATALOG_RUN_ID_REJECTED");
        var directory = Path.Combine(parent, "auth-ef8-" + runId);
        AuthCatalogRunnerBinding.Require(!Path.Exists(directory), "AUTH_CATALOG_EVIDENCE_RUN_EXISTS");
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("AUTH_CATALOG_OWNER_REQUIRED");
        acl.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        RequireOwnerAcl(directory, true);
        return new(directory);
    }
    internal void WriteNew(string file, byte[] bytes)
    {
        AuthCatalogRunnerBinding.Require(file is "candidate-reference.json" or "producer.trx" or "native-result.json" or "completion.json" or "admission.json",
            "AUTH_CATALOG_PRIVATE_FILENAME_REJECTED");
        RequireOwnerAcl(root, true);
        using var stream = new FileStream(Path.Combine(root, file), FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
        AuthCatalogRunnerBinding.ValidateLocalPath(Path.Combine(root, file), false);
    }
    internal void WriteJson(string file, object value) => WriteNew(file, JsonSerializer.SerializeToUtf8Bytes(value));
    internal void AdmitReference()
    {
        RequireOwnerAcl(root, true);
        var candidate = Path.Combine(root, "candidate-reference.json");
        AuthCatalogRunnerBinding.ValidateLocalPath(candidate, false);
        File.Move(candidate, Path.Combine(root, "reference.json"), false);
    }
    private static void RequireOwnerAcl(string path, bool protectedAcl)
    {
        AuthCatalogRunnerBinding.ValidateLocalPath(path, true);
        var acl = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("AUTH_CATALOG_OWNER_REQUIRED");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { owner.Value, "S-1-5-18", "S-1-5-32-544" };
        AuthCatalogRunnerBinding.Require(acl.GetOwner(typeof(SecurityIdentifier)).Value == owner.Value
            && (!protectedAcl || acl.AreAccessRulesProtected), "AUTH_CATALOG_EVIDENCE_OWNER_REJECTED");
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            AuthCatalogRunnerBinding.Require(rule.AccessControlType != AccessControlType.Allow
                || allowed.Contains(((SecurityIdentifier)rule.IdentityReference).Value), "AUTH_CATALOG_EVIDENCE_ACL_REJECTED");
    }
}

public sealed class AuthCatalogTransportPolicyTests
{
    private static string PostgresImage => "sha256:" + new string('a', 64);
    private static string PostgresDigest => "postgres@sha256:" + new string('b', 64);

    [Theory]
    [InlineData("missing-image")] [InlineData("missing-digest")] [InlineData("mutable-tag")]
    [InlineData("malformed-image")] [InlineData("wrong-repository")] [InlineData("malformed-digest")]
    public void Postgres_transport_requires_both_explicit_immutable_trusted_inputs(string defect)
    {
        string? image = PostgresImage, digest = PostgresDigest;
        switch (defect)
        {
            case "missing-image": image = null; break;
            case "missing-digest": digest = null; break;
            case "mutable-tag": image = "postgres:17-alpine"; break;
            case "malformed-image": image = "sha256:" + new string('A', 64); break;
            case "wrong-repository": digest = "untrusted/postgres@sha256:" + new string('b', 64); break;
            default: digest = "postgres@sha256:" + new string('b', 63); break;
        }
        Assert.Throws<InvalidOperationException>(() => AuthCatalogPostgresBinding.FromRequiredInputs(image, digest));
    }

    [Theory]
    [InlineData("missing-id")] [InlineData("wrong-id")] [InlineData("wrong-type-id")]
    [InlineData("missing-digest")] [InlineData("wrong-type-digests")] [InlineData("different-official-digest")]
    [InlineData("missing-platform")] [InlineData("wrong-os")] [InlineData("wrong-architecture")]
    [InlineData("duplicate-id")] [InlineData("wrong-root")]
    public void Postgres_preflight_rejects_missing_malformed_or_mismatched_local_metadata(string defect)
    {
        var fields = new Dictionary<string, object?>
        {
            ["id"] = PostgresImage, ["os"] = "linux", ["architecture"] = "amd64",
            ["repoDigests"] = new[] { PostgresDigest }
        };
        switch (defect)
        {
            case "missing-id": fields.Remove("id"); break;
            case "wrong-id": fields["id"] = "sha256:" + new string('c', 64); break;
            case "wrong-type-id": fields["id"] = 17; break;
            case "missing-digest": fields["repoDigests"] = Array.Empty<string>(); break;
            case "wrong-type-digests": fields["repoDigests"] = PostgresDigest; break;
            case "different-official-digest": fields["repoDigests"] = new[] { "postgres@sha256:" + new string('c', 64) }; break;
            case "missing-platform": fields.Remove("os"); break;
            case "wrong-os": fields["os"] = "windows"; break;
            case "wrong-architecture": fields["architecture"] = "arm64"; break;
        }
        var json = JsonSerializer.Serialize(fields);
        if (defect == "duplicate-id") json = "{\"id\":\"" + PostgresImage + "\"," + json[1..];
        if (defect == "wrong-root") json = "[]";
        using var document = JsonDocument.Parse(json);
        var binding = AuthCatalogPostgresBinding.FromRequiredInputs(PostgresImage, PostgresDigest);
        Assert.Throws<InvalidOperationException>(() => binding.RequireLocalImage(document.RootElement));
    }

    [Theory]
    [InlineData(null)] [InlineData("postgres:17-alpine")] [InlineData("different-immutable-id")]
    public void Created_Postgres_image_must_equal_the_preflight_admitted_immutable_image(string? actual)
    {
        if (actual == "different-immutable-id") actual = "sha256:" + new string('c', 64);
        var binding = AuthCatalogPostgresBinding.FromRequiredInputs(PostgresImage, PostgresDigest);
        Assert.Throws<InvalidOperationException>(() => binding.RequireCreatedImage(actual));
    }

    [Fact]
    public void Exact_Postgres_digest_and_created_image_are_admitted_without_a_mutable_tag()
    {
        var binding = AuthCatalogPostgresBinding.FromRequiredInputs(PostgresImage, PostgresDigest);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
            { id = PostgresImage, os = "linux", architecture = "amd64", repoDigests = new[] { PostgresDigest } }));
        binding.RequireLocalImage(document.RootElement);
        Assert.Equal(PostgresImage, binding.RequireCreatedImage(PostgresImage));
    }

    [Theory]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public void A_rejected_stream_never_returns_either_native_stream_for_export(bool outSecret, bool errorSecret)
    {
        var canary = Guid.NewGuid().ToString("N");
        (string Stdout, string Stderr) exported = (string.Empty, string.Empty);
        Assert.Throws<InvalidOperationException>(() => exported = AuthCatalogProducerGate.ApproveOutput(
            outSecret ? canary : "public-output", errorSecret ? canary : "public-error", value =>
            { if (value.Contains(canary, StringComparison.Ordinal)) throw new InvalidOperationException("PRIVATE_MATERIAL"); }));
        Assert.Empty(exported.Stdout); Assert.Empty(exported.Stderr);
    }

    [Theory]
    [InlineData(null)] [InlineData(1L)] [InlineData(27L)] [InlineData(28L)]
    public void Snapshot_presence_never_admits_an_incomplete_or_failed_native_producer(long? exit)
        => Assert.Throws<InvalidOperationException>(() => AuthCatalogProducerGate.RequireFinalSuccess(exit, exit is not null, true, GoodTrx()));
    [Fact]
    public void Exact_one_successful_linked_original_producer_is_required()
        => Assert.NotNull(AuthCatalogProducerGate.RequireFinalSuccess(0, true, true, GoodTrx()));
    [Theory]
    [InlineData("outcome")] [InlineData("identity")] [InlineData("link")] [InlineData("counter")]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("namespace")]
    public void Malformed_or_nonpassing_producer_evidence_fails_closed(string defect)
    {
        var text = Encoding.UTF8.GetString(GoodTrx());
        text = defect switch
        {
            "outcome" => text.Replace("outcome=\"Passed\"", "outcome=\"NotExecuted\"", StringComparison.Ordinal),
            "identity" => text.Replace(AuthCatalogProducerGate.ProducerMethod, "DifferentMethod", StringComparison.Ordinal),
            "link" => text.Replace("testId=\"00000000-0000-0000-0000-000000000001\"", "testId=\"00000000-0000-0000-0000-000000000003\"", StringComparison.Ordinal),
            "counter" => text.Replace("executed=\"1\"", "executed=\"0\"", StringComparison.Ordinal),
            "missing" => text.Replace("<TestEntries>", "<Unused>", StringComparison.Ordinal).Replace("</TestEntries>", "</Unused>", StringComparison.Ordinal),
            "duplicate" => text.Replace("</Results>", "<UnitTestResult /></Results>", StringComparison.Ordinal),
            _ => text.Replace("http://microsoft.com/schemas/VisualStudio/TeamTest/2010", "urn:wrong", StringComparison.Ordinal)
        };
        Assert.Throws<InvalidOperationException>(() => AuthCatalogProducerGate.RequireFinalSuccess(0, true, true, Encoding.UTF8.GetBytes(text)));
    }
    [Theory]
    [InlineData(false, true)] [InlineData(true, false)] [InlineData(false, false)]
    public void Native_or_cleanup_failure_never_admits_reference(bool producer, bool cleanup)
        => Assert.Throws<InvalidOperationException>(() => AuthCatalogProducerGate.RequireCleanup(producer, cleanup));
    [Theory]
    [InlineData("../reference.json")] [InlineData("reference.json/extra")] [InlineData("producer.trx/../request.json")]
    [InlineData("/run/secrets/request.json")] [InlineData("reference.json\n")]
    public void Catalog_reader_has_only_two_exact_public_evidence_names(string file)
        => Assert.False(AuthOnboardingAtomicFixture.IsAuthCatalogEvidenceFile(file));
    [Theory]
    [InlineData("tests/appsettings.json")] [InlineData("tests/worker-token.txt")] [InlineData("tests/Auth.exe")]
    [InlineData("tests/runtimes/win-x64/native/secret.pfx")]
    [InlineData("tests/runtimes/win-x64/native/native.dll")]
    [InlineData("tests/runtimes/osx-x64/native/native.dll")]
    public void Unneeded_configuration_credentials_and_Windows_apphosts_are_not_portable_payload(string path)
        => Assert.False(AuthCatalogRunnerBinding.PortablePayloadPath(path));
    private static byte[] GoodTrx()
    {
        const string test = "00000000-0000-0000-0000-000000000001", execution = "00000000-0000-0000-0000-000000000002";
        return Encoding.UTF8.GetBytes($"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results><UnitTestResult testId="{test}" executionId="{execution}" outcome="Passed" /></Results>
          <TestDefinitions><UnitTest id="{test}"><Execution id="{execution}" /><TestMethod className="{AuthCatalogProducerGate.ProducerClass}" name="{AuthCatalogProducerGate.ProducerMethod}" codeBase="/app/tests/Infrastructure.IntegrationTests.dll" /></UnitTest></TestDefinitions>
          <TestEntries><TestEntry testId="{test}" executionId="{execution}" /></TestEntries>
          <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" completed="0" inProgress="0" pending="0" /></ResultSummary>
        </TestRun>
        """);
    }
}
