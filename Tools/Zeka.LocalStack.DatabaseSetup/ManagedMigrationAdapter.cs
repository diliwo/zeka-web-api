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

    internal static async Task<MigrationRun> RunAsync(string operation, string service, string sourceCommit,
        string credential, string exportRoot, CancellationToken ct)
    {
        var context = NewOperation(operation, service, sourceCommit);
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] {
            "exec", "--depsfile", "./zeka-local-db-setup.deps.json",
            "--runtimeconfig", "./zeka-local-db-setup.runtimeconfig.json", "./zeka-db-migrate.dll",
            operation, "--service", service, "--target", "latest", "--operation-id", context.OperationId.ToString("D"),
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
            context.PrivateRoot, started, nativeExit, executionFailure, streams[0], streams[1]);
        return await CompleteAsync(context, receipt, exportRoot);
    }

    internal static MigrationOperationContext NewOperation(string operation, string service, string sourceCommit)
    {
        if (!OperatingSystem.IsLinux()) throw new SetupFailure("MIGRATION_LINUX_EVIDENCE_REQUIRED", null);
        if (operation is not ("plan" or "apply") || service is not ("auth" or "adminarea" or "client")
            || sourceCommit.Length != 40 || sourceCommit.Any(c => !Uri.IsHexDigit(c)))
            throw new SetupFailure("MIGRATION_OPERATION_IDENTITY_REJECTED", null);
        var id = Guid.NewGuid();
        var path = Path.Combine("/tmp", "zeka-m1-migration-" + id.ToString("N"));
        if (Path.Exists(path)) throw new SetupFailure("MIGRATION_PRIVATE_DIRECTORY_REJECTED", null, id);
        Directory.CreateDirectory(path, PrivateDirectoryMode);
        if (File.GetUnixFileMode(path) != PrivateDirectoryMode
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SetupFailure("MIGRATION_PRIVATE_DIRECTORY_REJECTED", null, id);
        return new MigrationOperationContext(id, operation, service, sourceCommit, path);
    }

    // This is also the focused test seam: it consumes the real CLI terminal format,
    // without substituting for the Linux publisher or accepting another operation.
    internal static async Task<MigrationRun> CompleteAsync(MigrationOperationContext context,
        NativeReceipt receipt, string exportRoot)
    {
        var receiptFile = "native-" + context.EvidenceFile;
        if (receipt.OperationId != context.OperationId || receipt.Operation != context.Operation
            || receipt.Service != context.Service || receipt.SourceCommit != context.SourceCommit
            || receipt.PrivateEvidenceRoot != context.PrivateRoot)
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
            var phases = root.GetProperty("phases");
            return new MigrationRun(receipt.NativeExit.Value, root.GetProperty("classification").GetString()!,
                phases.GetProperty("migration").GetProperty("status").GetString() == "passed",
                phases.GetProperty("manifestVerification").GetProperty("status").GetString() == "failed",
                context.OperationId, receiptFile);
        }
        catch (SetupFailure) { throw; }
        catch { throw Failure("MIGRATION_EVIDENCE_REJECTED"); }
    }

    private static void ValidateEnvelope(JsonElement root, MigrationOperationContext context, int exit)
    {
        UniqueProperties(root);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 19
            || root.GetProperty("schemaVersion").GetString() != "zeka-db-migrate-result/v0.1.0"
            || root.GetProperty("toolVersion").GetString() != "0.1.0"
            || root.GetProperty("operationId").GetGuid() != context.OperationId
            || root.GetProperty("operation").GetString() != context.Operation
            || root.GetProperty("service").GetString() != context.Service
            || root.GetProperty("sourceCommit").GetString() != context.SourceCommit
            || root.GetProperty("requestedTarget").GetString() != "latest"
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
    string SourceCommit, string PrivateRoot)
{
    public string EvidenceFile => Operation + "-" + OperationId.ToString("D") + ".json";
}
internal sealed record StreamObservation(bool Present, long? CharacterCount, string? Status, bool Withheld, bool Complete);
internal sealed record NativeReceipt(Guid OperationId, string Operation, string Service, string SourceCommit,
    string PrivateEvidenceRoot, bool NativeStarted, int? NativeExit, string? ExecutionFailure,
    StreamObservation StandardOutput, StreamObservation StandardError);
internal sealed record MigrationRun(int Exit, string Classification, bool MigrationPassed, bool ManifestFailed,
    Guid OperationId, string ReceiptFile);
internal sealed class SetupFailure(string code, int? nativeExit, Guid? operationId = null, string? receiptFile = null) : Exception
{
    public string Code { get; } = code;
    public int? NativeExit { get; } = nativeExit;
    public Guid? OperationId { get; } = operationId;
    public string? ReceiptFile { get; } = receiptFile;
}
