using Zeka.ToolingDiagnostics;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Zeka.LocalStack.DatabaseSetup;

// The CLI publishes with Linux descriptor-relative, no-follow, durable primitives. A
// Windows bind mount is only an export destination, never the publication root.
internal static class ManagedMigrationAdapter
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);
    internal const string AuthPredecessor = "20261018144506_BrowserSessionSnapshot";
    internal const string AuthTarget = "20261115195218_OnboardingRegistration";
    internal const string AuthOwnerIndexTarget = "20261124170000_OrganisationSingleActiveOwner";
    private const string AuthOwnerIndexMigrationHash = "8b2b5ad1ac51c7d0038e127b75791324c11bf4a76c699524b31feee084249400";
    private const string AuthOwnerIndexManifestHash = "93157072e70a23718e7f943f9e4340970e638da8347b06b4d672a47555e2baa0";
    private const string NoGrantHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AuthMigrationHash = "b68a058177c27b67e22c81e11cf6ec3c0eedbe69833937b534057bc2af7075e2";
    private const string AuthGrantHash = "b897bc6e7f933a538142696b81644d91c4d9439cb5ee7db1933106c52b9ac841";
    private const string AuthManifestHash = "6eca66966a8957a7dfc558d308992c2a92e9dd324ae3bcf2b232f583f314b944";

    internal static async Task<MigrationRun> RunAsync(string operation, string service, string sourceCommit,
        string credential, string exportRoot, CancellationToken ct, string requestedTarget = "latest",
        string? expectedDatabase = null)
    {
        var context = NewOperation(operation, service, sourceCommit, requestedTarget, expectedDatabase);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] {
            "exec", "--depsfile", "./zeka-local-db-setup.deps.json",
            "--runtimeconfig", "./zeka-local-db-setup.runtimeconfig.json", "./zeka-db-migrate.dll",
            operation, "--service", service, "--target", context.RequestedTarget, "--operation-id", context.OperationId.ToString("D"),
            "--evidence-root", context.PrivateRoot, "--evidence-file", context.EvidenceFile,
            "--lock-timeout-seconds", "10", "--credential-stdin" }) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        var started = false;
        int? nativeExit = null;
        Task<StreamObservation>? stdout = null;
        Task<StreamObservation>? stderr = null;
        string? executionFailure = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started) throw new InvalidOperationException();
            stdout = ObserveAsync(process.StandardOutput);
            stderr = ObserveAsync(process.StandardError);
            // Credentials never enter native arguments, environment values or receipts.
            await process.StandardInput.WriteLineAsync(credential.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            nativeExit = process.ExitCode;
        }
        catch (Exception error)
        {
            executionFailure = error is OperationCanceledException ? "MIGRATION_PROCESS_CANCELLED" :
                started ? "MIGRATION_PROCESS_EXECUTION_FAILED" : "MIGRATION_PROCESS_START_FAILED";
            if (started)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var stop = new CancellationTokenSource(DrainLimit);
                    await process.WaitForExitAsync(stop.Token);
                    nativeExit = process.ExitCode;
                }
                catch { /* Unknown execution state is retained, never interpreted as success. */ }
            }
        }
        var streams = await Task.WhenAll(DrainAsync(stdout), DrainAsync(stderr));
        if (executionFailure is null && streams.Any(stream => !stream.Complete))
            executionFailure = "MIGRATION_NATIVE_STREAM_INCOMPLETE";
        var receipt = new NativeReceipt(context.OperationId, context.Operation, context.Service, context.SourceCommit,
            context.PrivateRoot, started, nativeExit, executionFailure, streams[0], streams[1],
            context.RequestedTarget, context.ExpectedDatabase);
        return await CompleteAsync(context, receipt, exportRoot);
    }

    internal static MigrationOperationContext NewOperation(string operation, string service, string sourceCommit,
        string requestedTarget = "latest", string? expectedDatabase = null)
    {
        if (!OperatingSystem.IsLinux()) throw new SetupFailure("MIGRATION_LINUX_EVIDENCE_REQUIRED", null);
        if (operation is not ("plan" or "apply") || service is not ("auth" or "adminarea" or "client")
            || sourceCommit.Length != 40 || sourceCommit.Any(c => !Uri.IsHexDigit(c))
            || (requestedTarget != "latest" && (service != "auth" || requestedTarget is not (AuthPredecessor or AuthTarget or AuthOwnerIndexTarget)))
            || (expectedDatabase is not null && !SafeIdentifier(expectedDatabase, 63))
            || (service == "auth" && operation == "apply" && requestedTarget is (AuthTarget or AuthOwnerIndexTarget) && expectedDatabase is null))
            throw new SetupFailure("MIGRATION_OPERATION_IDENTITY_REJECTED", null);
        var id = Guid.NewGuid();
        var path = Path.Combine("/tmp", "zeka-m1-migration-" + id.ToString("N"));
        if (Path.Exists(path)) throw new SetupFailure("MIGRATION_PRIVATE_DIRECTORY_REJECTED", null, id);
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        if (File.GetUnixFileMode(path) != PrivateDirectoryMode
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SetupFailure("MIGRATION_PRIVATE_DIRECTORY_REJECTED", null, id);
        return new MigrationOperationContext(id, operation, service, sourceCommit, path, requestedTarget, expectedDatabase);
    }

    // This is also the focused test seam: it consumes the real CLI terminal format,
    // without substituting for the Linux publisher or accepting another operation.
    internal static async Task<MigrationRun> CompleteAsync(MigrationOperationContext context,
        NativeReceipt receipt, string exportRoot)
    {
        var receiptFile = "native-" + context.EvidenceFile;
        if (receipt.OperationId != context.OperationId || receipt.Operation != context.Operation
            || receipt.Service != context.Service || receipt.SourceCommit != context.SourceCommit
            || receipt.PrivateEvidenceRoot != context.PrivateRoot
            || receipt.RequestedTarget != context.RequestedTarget || receipt.ExpectedDatabase != context.ExpectedDatabase)
            throw new SetupFailure("MIGRATION_RECEIPT_IDENTITY_REJECTED", receipt.NativeExit, context.OperationId);
        try
        {
            await WriteExportAsync(Path.Combine(exportRoot, receiptFile), JsonSerializer.SerializeToUtf8Bytes(receipt));
        }
        catch
        {
            throw new SetupFailure("MIGRATION_RECEIPT_PERSIST_FAILED", receipt.NativeExit, context.OperationId, receiptFile);
        }
        SetupFailure Failure(string code) => new(code, receipt.NativeExit, context.OperationId, receiptFile);
        if (!receipt.NativeStarted || receipt.NativeExit is null)
            throw Failure(receipt.ExecutionFailure ?? "MIGRATION_NATIVE_STATE_UNCERTAIN");
        var source = Path.Combine(context.PrivateRoot, context.EvidenceFile);
        if (!File.Exists(source)) throw Failure("MIGRATION_EVIDENCE_MISSING");
        try
        {
            if ((File.GetAttributes(context.PrivateRoot) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0
                || (OperatingSystem.IsLinux() && (File.GetUnixFileMode(context.PrivateRoot) != PrivateDirectoryMode
                    || File.GetUnixFileMode(source) != PrivateFileMode))
                || new FileInfo(source).Length is <= 0 or > 262144)
                throw Failure("MIGRATION_EVIDENCE_REJECTED");
            var bytes = await File.ReadAllBytesAsync(source);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            ValidateEnvelope(root, context, receipt.NativeExit.Value);
            // Export the bytes actually published by the strict writer, including
            // first-failure evidence for nonzero exits. The caller still rejects it.
            await WriteExportAsync(Path.Combine(exportRoot, context.EvidenceFile), bytes);
            if (receipt.ExecutionFailure is not null) throw Failure(receipt.ExecutionFailure);
            if (!receipt.StandardOutput.Complete || !receipt.StandardError.Complete)
                throw Failure("MIGRATION_NATIVE_STREAM_INCOMPLETE");
            var phases = root.GetProperty("phases");
            return new MigrationRun(receipt.NativeExit.Value, root.GetProperty("classification").GetString()!,
                phases.GetProperty("migration").GetProperty("status").GetString() == "passed",
                phases.GetProperty("manifestVerification").GetProperty("status").GetString() == "failed",
                context.OperationId, receiptFile, root.TryGetProperty("postgresDiagnostic", out var diagnostic) ? SafePostgresDiagnostic.Parse(diagnostic) : null,
                context.RequestedTarget, root.GetProperty("resultCode").GetString()!, root.TryGetProperty("atomicAuth", out _),
                root.GetProperty("startingMigration").GetString(), root.GetProperty("observedFinalMigration").GetString());
        }
        catch (SetupFailure) { throw; }
        catch { throw Failure("MIGRATION_EVIDENCE_REJECTED"); }
    }

    private static void ValidateLegacyEnvelope(JsonElement root, MigrationOperationContext context, int exit)
    {
        UniqueProperties(root);
        if (root.TryGetProperty("postgresDiagnostic", out var postgres)) _ = SafePostgresDiagnostic.Parse(postgres);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != (root.TryGetProperty("postgresDiagnostic", out var pg) ? 20 : 19)
            || root.GetProperty("schemaVersion").GetString() != "zeka-db-migrate-result/v0.1.0"
            || root.GetProperty("toolVersion").GetString() != "0.1.0"
            || root.GetProperty("operationId").GetGuid() != context.OperationId
            || root.GetProperty("operation").GetString() != context.Operation
            || root.GetProperty("service").GetString() != context.Service
            || root.GetProperty("sourceCommit").GetString() != context.SourceCommit
            || root.GetProperty("requestedTarget").GetString() != context.RequestedTarget
            || root.GetProperty("logicalDatabase").GetString() != (context.Service switch {
                "auth" => "AuthManagement", "adminarea" => "AdminAreaManagement", "client" => "ClientManagement", _ => "" })
            || root.GetProperty("classification").GetString() is not ("REJECTED" or "ALREADY_CURRENT" or
                "FORWARD_APPROVED" or "MIGRATION_STATE_REQUIRES_INSPECTION" or "DOWN_TARGET_REJECTED")
            || (exit == 0 && root.GetProperty("classification").GetString() is not ("ALREADY_CURRENT" or "FORWARD_APPROVED"))
            || !ResultMatchesExit(root.GetProperty("resultCode").GetString(), exit)
            || root.GetProperty("endedAt").GetDateTimeOffset() < root.GetProperty("startedAt").GetDateTimeOffset())
            throw new InvalidDataException();
        var phases = root.GetProperty("phases");
        if (phases.EnumerateObject().Count() != 4) throw new InvalidDataException();
        foreach (var phase in new[] { "migration", "bootstrapReconciliation", "manifestVerification", "runtimeReadiness" })
        {
            var value = phases.GetProperty(phase);
            if (value.EnumerateObject().Count() != 1 || value.GetProperty("status").GetString()
                is not ("not_started" or "started" or "passed" or "failed" or "untested")) throw new InvalidDataException();
        }
        if ((exit == 0 && phases.EnumerateObject().Any(phase => phase.Value.GetProperty("status").GetString() == "failed"))
            || (exit == 27 && phases.GetProperty("manifestVerification").GetProperty("status").GetString() != "failed"))
            throw new InvalidDataException();
        // Demand the full published envelope, including nullable migration fields.
        foreach (var name in new[] { "startingMigration", "resolvedTarget", "observedFinalMigration" })
        {
            var migration = root.GetProperty(name);
            if (migration.ValueKind != JsonValueKind.Null && (migration.ValueKind != JsonValueKind.String
                || !SafeIdentifier(migration.GetString(), 256))) throw new InvalidDataException();
        }
        _ = root.GetProperty("advisoryLockAcquired").GetBoolean();
        var roles = root.GetProperty("roles");
        if (roles.EnumerateObject().Count() != 7
            || roles.GetProperty("expectedSessionUser").GetString() != "zeka_" + context.Service + "_migrator"
            || roles.GetProperty("expectedCurrentUser").GetString() != "zeka_" + context.Service + "_owner")
            throw new InvalidDataException();
        foreach (var name in new[] { "observedSessionUser", "observedCurrentUser" })
        {
            var role = roles.GetProperty(name);
            if (role.ValueKind != JsonValueKind.Null && (role.ValueKind != JsonValueKind.String
                || !SafeIdentifier(role.GetString(), 63))) throw new InvalidDataException();
        }
        foreach (var name in new[] { "restrictedAttributes", "matchingOwnerMembership", "ownerAssumption" })
            _ = roles.GetProperty(name).GetBoolean();
        foreach (var caveat in root.GetProperty("caveats").EnumerateArray())
            if (caveat.GetString() is not (
                "Bootstrap reconciliation and runtime readiness are separate host phases." or
                "The result is host-produced evidence and is not an independently signed production attestation." or
                "The exact current target predates the manifest migration; final manifest verification is not applicable yet." or
                "Plan mode performed no persistent migration mutation." or
                "EF migration work started; inspect history and catalog state before any retry." or
                "The exact forward target predates the manifest migration; final manifest verification is not applicable yet."))
                throw new InvalidDataException();
    }

    private static bool IsAtomicContext(MigrationOperationContext context) => context.Service == "auth"
        && context.Operation == "apply" && context.RequestedTarget is (AuthTarget or AuthOwnerIndexTarget);

    private static void ValidateEnvelope(JsonElement root, MigrationOperationContext context, int exit)
    {
        UniqueProperties(root);
        var earlyCredentialFailure = exit == 21 && !root.TryGetProperty("atomicAuth", out _)
            && root.GetProperty("classification").GetString() == "REJECTED"
            && root.GetProperty("resultCode").GetString() == "CREDENTIAL_REJECTED";
        if (IsAtomicContext(context) && !earlyCredentialFailure) ValidateAtomicEnvelope(root, context, exit);
        else
        {
            if (root.TryGetProperty("atomicAuth", out _)) throw new InvalidDataException();
            ValidateLegacyEnvelope(root, context, exit);
            if (earlyCredentialFailure) ValidateEarlyCredentialFailure(root);
            if (exit == 0) ValidateSuccessfulLegacy(root, context);
        }
    }

    private static void ValidateEarlyCredentialFailure(JsonElement root)
    {
        // The unchanged CLI can refuse stdin acquisition before entering either engine.
        // This is the only legacy-shaped terminal allowed for an exact atomic apply.
        if (root.TryGetProperty("postgresDiagnostic", out _) || root.GetProperty("advisoryLockAcquired").GetBoolean())
            throw new InvalidDataException();
        foreach (var name in new[] { "startingMigration", "resolvedTarget", "observedFinalMigration" })
            if (root.GetProperty(name).ValueKind != JsonValueKind.Null) throw new InvalidDataException();
        var roles = root.GetProperty("roles");
        foreach (var name in new[] { "observedSessionUser", "observedCurrentUser" })
            if (roles.GetProperty(name).ValueKind != JsonValueKind.Null) throw new InvalidDataException();
        foreach (var name in new[] { "restrictedAttributes", "matchingOwnerMembership", "ownerAssumption" })
            if (roles.GetProperty(name).GetBoolean()) throw new InvalidDataException();
        var phases = root.GetProperty("phases");
        foreach (var name in new[] { "migration", "bootstrapReconciliation", "manifestVerification" })
            if (phases.GetProperty(name).GetProperty("status").GetString() != "not_started") throw new InvalidDataException();
        if (phases.GetProperty("runtimeReadiness").GetProperty("status").GetString() != "untested")
            throw new InvalidDataException();
        var caveats = root.GetProperty("caveats");
        if (caveats.GetArrayLength() != 1 || caveats[0].GetString() != "Bootstrap reconciliation and runtime readiness are separate host phases.")
            throw new InvalidDataException();
    }

    private static void ValidateSuccessfulLegacy(JsonElement root, MigrationOperationContext context)
    {
        ValidateSuccessfulIdentity(root);
        var resolved = root.GetProperty("resolvedTarget").GetString();
        var starting = root.GetProperty("startingMigration").GetString();
        var final = root.GetProperty("observedFinalMigration").GetString();
        var classification = root.GetProperty("classification").GetString();
        var phases = root.GetProperty("phases");
        var migration = phases.GetProperty("migration").GetProperty("status").GetString();
        var manifest = phases.GetProperty("manifestVerification").GetProperty("status").GetString();
        var expectedManifest = context.Service == "auth" && context.RequestedTarget == AuthPredecessor ? "untested" : "passed";
        if (!SafeIdentifier(resolved, 256)
            || (context.RequestedTarget != "latest" && resolved != context.RequestedTarget)
            || phases.GetProperty("bootstrapReconciliation").GetProperty("status").GetString() != "not_started"
            || phases.GetProperty("runtimeReadiness").GetProperty("status").GetString() != "untested")
            throw new InvalidDataException();
        if (classification == "ALREADY_CURRENT")
        {
            if (starting != resolved || final != resolved || migration != "not_started"
                || manifest != expectedManifest) throw new InvalidDataException();
        }
        else if (context.Operation == "plan")
        {
            if (migration != "not_started" || manifest != "not_started" || final != starting)
                throw new InvalidDataException();
        }
        else if (migration != "passed" || final != resolved || manifest != expectedManifest)
            throw new InvalidDataException();
    }

    private static void ValidateSuccessfulIdentity(JsonElement root)
    {
        var roles = root.GetProperty("roles");
        if (!root.GetProperty("advisoryLockAcquired").GetBoolean()
            || roles.GetProperty("observedSessionUser").GetString() != roles.GetProperty("expectedSessionUser").GetString()
            || roles.GetProperty("observedCurrentUser").GetString() != roles.GetProperty("expectedCurrentUser").GetString()
            || !roles.GetProperty("restrictedAttributes").GetBoolean()
            || !roles.GetProperty("matchingOwnerMembership").GetBoolean()
            || !roles.GetProperty("ownerAssumption").GetBoolean()) throw new InvalidDataException();
    }

    private static void ValidateAtomicEnvelope(JsonElement root, MigrationOperationContext context, int exit)
    {
        var ownerIndex = context.RequestedTarget == AuthOwnerIndexTarget;
        var target = context.RequestedTarget;
        var predecessor = ownerIndex ? AuthTarget : AuthPredecessor;
        var hasDiagnostic = root.TryGetProperty("postgresDiagnostic", out var diagnostic);
        if (hasDiagnostic) _ = SafePostgresDiagnostic.Parse(diagnostic);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != (hasDiagnostic ? 21 : 20)
            || root.GetProperty("schemaVersion").GetString() != "zeka-db-migrate-result/v0.1.0"
            || root.GetProperty("toolVersion").GetString() != "0.1.0"
            || root.GetProperty("sourceCommit").GetString() != context.SourceCommit
            || root.GetProperty("operationId").GetGuid() != context.OperationId
            || root.GetProperty("operation").GetString() != "apply"
            || root.GetProperty("service").GetString() != "auth"
            || root.GetProperty("logicalDatabase").GetString() != "AuthManagement"
            || root.GetProperty("requestedTarget").GetString() != target
            || root.GetProperty("resolvedTarget").GetString() != target
            || !SafeIdentifier(context.ExpectedDatabase, 63)
            || root.GetProperty("endedAt").GetDateTimeOffset() < root.GetProperty("startedAt").GetDateTimeOffset())
            throw new InvalidDataException();
        foreach (var name in new[] { "startingMigration", "observedFinalMigration" })
        {
            var value = root.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Null && !SafeIdentifier(value.GetString(), 256))
                throw new InvalidDataException();
        }
        _ = root.GetProperty("advisoryLockAcquired").GetBoolean();
        var roles = root.GetProperty("roles");
        ExactProperties(roles, "expectedSessionUser", "observedSessionUser", "expectedCurrentUser", "observedCurrentUser",
            "restrictedAttributes", "matchingOwnerMembership", "ownerAssumption");
        if (roles.GetProperty("expectedSessionUser").GetString() != "zeka_auth_migrator"
            || roles.GetProperty("expectedCurrentUser").GetString() != "zeka_auth_owner") throw new InvalidDataException();
        foreach (var name in new[] { "observedSessionUser", "observedCurrentUser" })
        {
            var value = roles.GetProperty(name);
            if (value.ValueKind != JsonValueKind.Null && !SafeIdentifier(value.GetString(), 63)) throw new InvalidDataException();
        }
        foreach (var name in new[] { "restrictedAttributes", "matchingOwnerMembership", "ownerAssumption" })
            _ = roles.GetProperty(name).GetBoolean();
        var phases = root.GetProperty("phases");
        ExactProperties(phases, "migration", "bootstrapReconciliation", "manifestVerification", "runtimeReadiness");
        foreach (var property in phases.EnumerateObject())
        {
            ExactProperties(property.Value, "status");
            if (property.Value.GetProperty("status").GetString() is not
                ("not_started" or "started" or "passed" or "failed" or "untested" or "not_needed" or "rolled_back"))
                throw new InvalidDataException();
        }
        if (phases.GetProperty("runtimeReadiness").GetProperty("status").GetString() != "untested")
            throw new InvalidDataException();
        var atomic = root.GetProperty("atomicAuth");
        ExactProperties(atomic, "migrationSqlSha256", "grantSqlSha256", "manifestSha256", "commitAttempted",
            "commitAcknowledged", "rollbackConfirmed", "durableState", "replayAttempted", "databaseName", "backendPid", "failedStage");
        if (atomic.GetProperty("migrationSqlSha256").GetString() != (ownerIndex ? AuthOwnerIndexMigrationHash : AuthMigrationHash)
            || atomic.GetProperty("grantSqlSha256").GetString() != (ownerIndex ? NoGrantHash : AuthGrantHash)
            || atomic.GetProperty("manifestSha256").GetString() != (ownerIndex ? AuthOwnerIndexManifestHash : AuthManifestHash)
            || atomic.GetProperty("replayAttempted").GetBoolean()) throw new InvalidDataException();
        var attempted = atomic.GetProperty("commitAttempted").GetBoolean();
        var acknowledged = atomic.GetProperty("commitAcknowledged").GetBoolean();
        var rollback = atomic.GetProperty("rollbackConfirmed");
        if (rollback.ValueKind is not (JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)
            || (acknowledged && !attempted)) throw new InvalidDataException();
        var database = atomic.GetProperty("databaseName");
        if (database.ValueKind != JsonValueKind.Null && database.GetString() != context.ExpectedDatabase)
            throw new InvalidDataException();
        var pid = atomic.GetProperty("backendPid");
        if (pid.ValueKind != JsonValueKind.Null && pid.GetInt32() <= 0) throw new InvalidDataException();
        var stage = atomic.GetProperty("failedStage");
        if (stage.ValueKind != JsonValueKind.Null && stage.GetString() is not (
            "ARTIFACT_BINDING" or "CONNECTION" or "TRANSACTION" or "ADVISORY_TRANSACTION_LOCK" or "ROLE_IDENTITY"
            or "HISTORY_AND_SCHEMA_RECHECK" or "MIGRATION_SQL" or "APPROVED_GRANTS" or "EXACT_SCHEMA_VERIFICATION"
            or "FULL_V20_VERIFICATION" or "FULL_V21_VERIFICATION" or "COMMIT")) throw new InvalidDataException();
        if ((stage.GetString() == "FULL_V21_VERIFICATION" && !ownerIndex)
            || (stage.GetString() == "FULL_V20_VERIFICATION" && ownerIndex)) throw new InvalidDataException();
        var durable = atomic.GetProperty("durableState").GetString();
        if (durable is not ("NOT_CHECKED" or "ROLLED_BACK" or "INCONCLUSIVE" or "COMMITTED_VERIFIED" or "BASELINE_VERIFIED"))
            throw new InvalidDataException();
        var classification = root.GetProperty("classification").GetString();
        var code = root.GetProperty("resultCode").GetString();

        foreach (var caveat in root.GetProperty("caveats").EnumerateArray())
            if (caveat.GetString() is not (
                "Only the exact Auth predecessor-to-onboarding SQL and its pinned grants are executable." or
                "bootstrapReconciliation reports only the hash-pinned onboarding grant subset; no full bootstrap reconciliation runs here." or
                "SourceCommit identifies the base; the separately admitted source snapshot and binary hashes bind dirty source." or
                "Provider acceptance and retained-database admission remain separate from source and disposable-test evidence." or
                "Commit submission began; an exception or cancellation cannot establish rollback. No replay was attempted." or
                "Rollback acknowledgment was unavailable; inspect durable state before any further action." or
                "Context disposal was not acknowledged." or "Transaction disposal was not acknowledged." or
                "Connection disposal was not acknowledged." or
                "Durable inspection is read-only on a fresh connection; inspection does not authorize a retry."))
                throw new InvalidDataException();
        if (exit != 0)
        {
            ValidateFailedAtomic(root, context, exit);
            return; // A correlated native failure is exported, never upgraded to success.
        }
        ValidateSuccessfulIdentity(root);
        if (code != "SUCCESS" || classification is not ("ATOMIC_AUTH_APPLIED" or "ATOMIC_AUTH_ALREADY_CURRENT")
            || !attempted || !acknowledged || rollback.ValueKind != JsonValueKind.Null
            || durable != "COMMITTED_VERIFIED" || database.GetString() != context.ExpectedDatabase
            || pid.ValueKind != JsonValueKind.Number || stage.ValueKind != JsonValueKind.Null
            || root.GetProperty("observedFinalMigration").GetString() != target
            || phases.GetProperty("manifestVerification").GetProperty("status").GetString() != "passed")
            throw new InvalidDataException();
        var current = classification == "ATOMIC_AUTH_ALREADY_CURRENT";
        if (root.GetProperty("startingMigration").GetString() != (current ? target : predecessor)
            || phases.GetProperty("migration").GetProperty("status").GetString() != (current ? "not_needed" : "passed")
            || phases.GetProperty("bootstrapReconciliation").GetProperty("status").GetString() != (current || ownerIndex ? "not_needed" : "passed"))
            throw new InvalidDataException();
    }

    private static void ValidateFailedAtomic(JsonElement root, MigrationOperationContext context, int exit)
    {
        var atomic = root.GetProperty("atomicAuth");
        var stage = atomic.GetProperty("failedStage").GetString();
        var step = stage switch
        {
            "ARTIFACT_BINDING" => 0, "CONNECTION" => 1, "TRANSACTION" => 2,
            "ADVISORY_TRANSACTION_LOCK" => 3, "ROLE_IDENTITY" => 4,
            "HISTORY_AND_SCHEMA_RECHECK" => 5, "MIGRATION_SQL" => 6,
            "APPROVED_GRANTS" => 7, "EXACT_SCHEMA_VERIFICATION" => 8,
            "FULL_V20_VERIFICATION" or "FULL_V21_VERIFICATION" => 9, "COMMIT" => 10,
            _ => throw new InvalidDataException()
        };
        var classification = root.GetProperty("classification").GetString();
        var code = root.GetProperty("resultCode").GetString();
        var attempted = atomic.GetProperty("commitAttempted").GetBoolean();
        var rollback = atomic.GetProperty("rollbackConfirmed").ValueKind;
        var durable = atomic.GetProperty("durableState").GetString();
        if (atomic.GetProperty("commitAcknowledged").GetBoolean()) throw new InvalidDataException();

        // The producer never attempts rollback after submitting COMMIT. Even a durable
        // readback cannot turn its unacknowledged commit into REJECTED or success.
        if (step == 10 || attempted || classification == "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION")
        {
            if (step != 10 || !attempted || rollback != JsonValueKind.Null || exit != 28
                || classification != "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION" || code != classification
                || durable is not ("INCONCLUSIVE" or "BASELINE_VERIFIED" or "COMMITTED_VERIFIED"))
                throw new InvalidDataException();
        }
        else
        {
            int? refusalExit = (stage, code) switch
            {
                (_, "ATOMIC_AUTH_FAILED") => 27,
                ("ARTIFACT_BINDING", "ATOMIC_AUTH_EXACT_TARGET_REQUIRED" or "ATOMIC_AUTH_DATABASE_BINDING_REQUIRED") => 20,
                ("CONNECTION", "ATOMIC_AUTH_POSTGRESQL17_REQUIRED") => 24,
                ("ROLE_IDENTITY", "ATOMIC_AUTH_IDENTITY_REJECTED" or "ATOMIC_AUTH_OWNER_ASSUMPTION_REJECTED") => 24,
                ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_COMPILED_HISTORY_REJECTED" or "ATOMIC_AUTH_HISTORY_REJECTED"
                    or "ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED") => 25,
                ("HISTORY_AND_SCHEMA_RECHECK", "ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED") => 27,
                ("EXACT_SCHEMA_VERIFICATION", "ATOMIC_AUTH_FINAL_HISTORY_REJECTED") => 25,
                _ => null
            };
            if (refusalExit is null) throw new InvalidDataException();
            var valid = classification switch
            {
                "REJECTED" => step <= 2 && rollback == JsonValueKind.Null
                    && durable == "NOT_CHECKED" && exit == refusalExit,
                "ATOMIC_AUTH_ROLLED_BACK" => step >= 2 && rollback == JsonValueKind.True
                    && durable == "ROLLED_BACK" && exit == refusalExit,
                "ATOMIC_AUTH_REQUIRES_INSPECTION" => step >= 2 && rollback == JsonValueKind.False
                    && durable == "NOT_CHECKED" && exit == 28,
                _ => false
            };
            if (!valid) throw new InvalidDataException();
        }

        var database = atomic.GetProperty("databaseName");
        var pid = atomic.GetProperty("backendPid");
        if ((step == 0 && (database.ValueKind != JsonValueKind.Null || pid.ValueKind != JsonValueKind.Null))
            || (step >= 1 && database.ValueKind != JsonValueKind.String)
            || ((step >= 2 || code == "ATOMIC_AUTH_POSTGRESQL17_REQUIRED") && pid.ValueKind != JsonValueKind.Number))
            throw new InvalidDataException();
        var locked = root.GetProperty("advisoryLockAcquired").GetBoolean();
        if ((step <= 2 && locked) || (step >= 4 && !locked)) throw new InvalidDataException();
        var roles = root.GetProperty("roles");
        if (step < 4 && (roles.GetProperty("observedSessionUser").ValueKind != JsonValueKind.Null
            || roles.GetProperty("observedCurrentUser").ValueKind != JsonValueKind.Null
            || roles.GetProperty("restrictedAttributes").GetBoolean()
            || roles.GetProperty("matchingOwnerMembership").GetBoolean()
            || roles.GetProperty("ownerAssumption").GetBoolean())) throw new InvalidDataException();
        var session = roles.GetProperty("observedSessionUser").GetString();
        var current = roles.GetProperty("observedCurrentUser").GetString();
        var restricted = roles.GetProperty("restrictedAttributes").GetBoolean();
        var membership = roles.GetProperty("matchingOwnerMembership").GetBoolean();
        var owner = roles.GetProperty("ownerAssumption").GetBoolean();
        if (step >= 5 || owner) ValidateSuccessfulIdentity(root);
        // ROLE_IDENTITY has only two incomplete observations: no completed read,
        // or the accepted initial migrator pair before the owner read completes.
        if (step == 4 && code == "ATOMIC_AUTH_FAILED" && !owner
            && !((session is null && current is null && !restricted && !membership)
                || (session == "zeka_auth_migrator" && current == "zeka_auth_migrator" && restricted && membership)))
            throw new InvalidDataException();
        if ((code == "ATOMIC_AUTH_IDENTITY_REJECTED" && (session is null || current is null || owner
                || (session == "zeka_auth_migrator" && current == "zeka_auth_migrator" && restricted && membership)))
            || (code == "ATOMIC_AUTH_OWNER_ASSUMPTION_REJECTED" && (session is null || current is null
                || !restricted || !membership || owner
                || (session == "zeka_auth_migrator" && current == "zeka_auth_owner"))))
            throw new InvalidDataException();

        ValidateFailedAtomicPhases(root, context, step, rollback == JsonValueKind.True);
    }

    private static void ValidateFailedAtomicPhases(JsonElement root, MigrationOperationContext context, int step, bool rolledBack)
    {
        var ownerIndex = context.RequestedTarget == AuthOwnerIndexTarget;
        var target = context.RequestedTarget;
        var predecessor = ownerIndex ? AuthTarget : AuthPredecessor;
        var phases = root.GetProperty("phases");
        var migration = phases.GetProperty("migration").GetProperty("status").GetString();
        var grants = phases.GetProperty("bootstrapReconciliation").GetProperty("status").GetString();
        var manifest = phases.GetProperty("manifestVerification").GetProperty("status").GetString();
        var started = rolledBack ? "rolled_back" : "started";
        var passed = rolledBack ? "rolled_back" : "passed";
        var starting = root.GetProperty("startingMigration").GetString();
        var final = root.GetProperty("observedFinalMigration").GetString();
        var code = root.GetProperty("resultCode").GetString();
        var completed = (migration == passed && grants == (ownerIndex ? "not_needed" : passed) && starting == predecessor)
            || (migration == "not_needed" && grants == "not_needed" && starting == target);
        var valid = step switch
        {
            <= 5 => migration == "not_started" && grants == "not_started" && manifest == "not_started"
                && final is null && (step == 5 || starting is null),
            6 => migration == started && grants == "not_started" && manifest == "not_started"
                && starting == predecessor && final is null,
            7 => migration == passed && (ownerIndex ? grants == "not_needed" : grants == started || grants == passed) && manifest == "not_started"
                && starting == predecessor && final is null,
            8 => completed && manifest == "not_started" && final is null,
            9 => completed && (manifest is "failed" or "passed") && final == target,
            10 => completed && manifest == "passed" && final == target,
            _ => false
        };
        if (!valid
            || (code == "ATOMIC_AUTH_COMPILED_HISTORY_REJECTED" && starting is not null)
            || (code is "ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED" or "ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED"
                && starting != predecessor && starting != target)) throw new InvalidDataException();
    }

    private static void ExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(expected)) throw new InvalidDataException();
    }

    private static bool SafeIdentifier(string? value, int maximum) => value is { Length: > 0 }
        && value.Length <= maximum && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) UniqueProperties(child);
    }

    private static bool ResultMatchesExit(string? code, int exit) => (exit, code) is
        (0, "SUCCESS") or (21, "CREDENTIAL_REJECTED") or (22, "CONNECTION_REJECTED") or
        (23, "LOCK_UNAVAILABLE") or (24, "IDENTITY_REJECTED" or "OWNER_ASSUMPTION_REJECTED") or
        (25, "MIGRATION_HISTORY_REJECTED" or "TARGET_REJECTED") or (26, "DOWN_TARGET_REJECTED") or
        (27, "MANIFEST_REJECTED") or (28, "MIGRATION_STATE_REQUIRES_INSPECTION") or
        (30, "OPERATION_CANCELLED" or "INTERNAL_FAILURE");

    internal static async Task<StreamObservation> ObserveAsync(TextReader reader)
    {
        var buffer = new char[1024];
        var candidate = new StringBuilder();
        long count = 0;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            count += read;
            if (count <= 128) candidate.Append(buffer, 0, read);
            else candidate.Clear();
        }
        var status = count <= 128 ? candidate.ToString().Trim() : "";
        var allowed = status is "INVALID_INPUT" or "EVIDENCE_PATH_REJECTED" or "EVIDENCE_PUBLISH_REJECTED" or
            "SUCCESS" or "CREDENTIAL_REJECTED" or "CONNECTION_REJECTED" or "LOCK_UNAVAILABLE" or
            "IDENTITY_REJECTED" or "OWNER_ASSUMPTION_REJECTED" or "MIGRATION_HISTORY_REJECTED" or
            "TARGET_REJECTED" or "DOWN_TARGET_REJECTED" or "MANIFEST_REJECTED" or
            "MIGRATION_STATE_REQUIRES_INSPECTION" or "OPERATION_CANCELLED" or "INTERNAL_FAILURE";
        return new StreamObservation(count != 0, count, allowed ? status : null, count != 0 && !allowed, true);
    }

    private static async Task<StreamObservation> DrainAsync(Task<StreamObservation>? task)
    {
        if (task is null) return new StreamObservation(false, 0, null, false, true);
        try { return await task.WaitAsync(DrainLimit); }
        catch { return new StreamObservation(true, null, null, true, false); }
    }

    private static async Task WriteExportAsync(string path, byte[] bytes)
    {
        // No overwrite: all artifacts are correlated with a fresh operation ID.
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
        file.Flush(flushToDisk: true);
    }
}

internal sealed record MigrationOperationContext(Guid OperationId, string Operation, string Service,
    string SourceCommit, string PrivateRoot, string RequestedTarget = "latest", string? ExpectedDatabase = null)
{
    public string EvidenceFile => Operation + "-" + OperationId.ToString("D") + ".json";
}
internal sealed record StreamObservation(bool Present, long? CharacterCount, string? Status, bool Withheld, bool Complete);
internal sealed record NativeReceipt(Guid OperationId, string Operation, string Service, string SourceCommit,
    string PrivateEvidenceRoot, bool NativeStarted, int? NativeExit, string? ExecutionFailure,
    StreamObservation StandardOutput, StreamObservation StandardError, string RequestedTarget = "latest", string? ExpectedDatabase = null);
internal sealed record MigrationRun(int Exit, string Classification, bool MigrationPassed, bool ManifestFailed,
    Guid OperationId, string ReceiptFile, SafePostgresDiagnostic? PostgresDiagnostic = null,
    string RequestedTarget = "latest", string ResultCode = "SUCCESS", bool AtomicAuth = false,
    string? StartingMigration = null, string? ObservedFinalMigration = null)
{
    public bool IsAcknowledgedCurrent => Exit == 0 && ResultCode == "SUCCESS"
        && Classification == (AtomicAuth ? "ATOMIC_AUTH_ALREADY_CURRENT" : "ALREADY_CURRENT");
}
internal sealed class SetupFailure(string code, int? nativeExit, Guid? operationId = null, string? receiptFile = null) : Exception, ISafePostgresDiagnosticFailure
{
    public SafePostgresDiagnostic? PostgresDiagnostic { get; init; }
    public string Code { get; } = code;
    public int? NativeExit { get; } = nativeExit;
    public Guid? OperationId { get; } = operationId;
    public string? ReceiptFile { get; } = receiptFile;
}
