using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Reflection.Metadata;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Npgsql;

namespace Zeka.OwnerSqlEvidence;

/// <summary>
/// Opt-in CI observation of actual handwritten owner-tool SQL execution. Never records SQL,
/// parameters, connection strings or database rows. This is execution evidence, not an assertion
/// that the caller's subsequent policy assertions or every server-side branch passed.
/// </summary>
internal static class OwnerSqlExecution
{
    private static readonly object WriteLock = new();

    internal static bool IsEnabled => Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE") == "1";

    internal static (Action Complete, Action<PostgresException> Fail, Action<OperationCanceledException> Cancel) ObserveAuthCommand(
        DbCommand command, string file, int line)
    {
        var observation = Prepare(command, file, line)
            ?? throw new InvalidOperationException("OWNER_SQL_EVIDENCE_CONFIGURATION_MISSING");
        return (() => observation.Write("success"), error => PreserveFailure(observation, error),
            error => PreserveTerminal(observation, error, "cancelled"));
    }

    internal static async Task<DbDataReader> ExecuteReaderAsync(DbCommand command, CancellationToken token,
        [CallerFilePath] string producerFile = "", [CallerLineNumber] int producerLine = 0)
    {
        var observation = Prepare(command, producerFile, producerLine);
        try
        {
            var result = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            return observation is null ? result : ObserveReaderCompletion(result,
                () => observation.Write("success"), exception => PreserveFailure(observation, exception));
        }
        catch (PostgresException exception)
        {
            PreserveFailure(observation, exception);
            throw;
        }
    }

    internal static async Task<object?> ExecuteScalarAsync(DbCommand command, CancellationToken token,
        [CallerFilePath] string producerFile = "", [CallerLineNumber] int producerLine = 0)
    {
        var observation = Prepare(command, producerFile, producerLine);
        try
        {
            var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            observation?.Write("success");
            return result;
        }
        catch (PostgresException exception)
        {
            PreserveFailure(observation, exception);
            throw;
        }
    }

    internal static async Task<int> ExecuteNonQueryAsync(DbCommand command, CancellationToken token,
        [CallerFilePath] string producerFile = "", [CallerLineNumber] int producerLine = 0)
    {
        var observation = Prepare(command, producerFile, producerLine);
        try
        {
            var result = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            observation?.Write("success");
            return result;
        }
        catch (PostgresException exception)
        {
            PreserveFailure(observation, exception);
            throw;
        }
    }

    internal static async Task<int> ExecuteBootstrapNonQueryAsync(DbCommand command, CancellationToken token,
        string service, [CallerFilePath] string producerFile = "", [CallerLineNumber] int producerLine = 0)
    {
        var observation = Prepare(command, producerFile, producerLine);
        Observation? bootstrap = null;
        if (observation is not null)
        {
            if (service is not ("auth" or "adminarea" or "client"))
                throw new InvalidOperationException("OWNER_SQL_EVIDENCE_BOOTSTRAP_REJECTED");
            var relative = "Deployments/database/bootstrap-" + service + "-roles.sql";
            var root = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_SOURCE_ROOT")!;
            var source = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("OWNER_SQL_EVIDENCE_BOOTSTRAP_REJECTED");
            var digest = SHA256.HashData(File.ReadAllBytes(source));
            // This allowlisted batch contains no supplied credential values. Only its digest is
            // compared; neither the SQL nor any command/connection parameter is serialized.
            if (!CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText))))
                throw new InvalidOperationException("OWNER_SQL_EVIDENCE_BOOTSTRAP_SOURCE_MISMATCH");
            bootstrap = new Observation("bootstrap:" + relative + ":batch",
                Convert.ToHexString(digest).ToLowerInvariant(), observation.QueryHash, observation.OutputRoot);
        }
        try
        {
            var result = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            observation?.Write("success");
            bootstrap?.Write("success");
            return result;
        }
        catch (PostgresException exception)
        {
            PreserveFailure(observation, exception);
            PreserveFailure(bootstrap, exception);
            throw;
        }
    }

    // Callers invoke this only after their existing row/identity assertions have passed.
    // The original cancellation budget applies to every remaining server result.
    internal static async Task CompleteReaderAsync(DbDataReader reader, CancellationToken token)
    {
        do { while (await reader.ReadAsync(token).ConfigureAwait(false)) { } }
        while (await reader.NextResultAsync(token).ConfigureAwait(false));
    }

    // Success means every result stream reached EOF, not merely that a reader was opened.
    // Disposing a partial reader deliberately provides no successful coverage receipt.
    internal static DbDataReader ObserveReaderCompletion(DbDataReader reader, Action completed,
        Action<PostgresException> failed) => new CompletionReader(reader, completed, failed);

    private sealed class CompletionReader(DbDataReader inner, Action completed, Action<PostgresException> failed) : DbDataReader
    {
        private int terminal;
        private void Complete()
        {
            if (Interlocked.CompareExchange(ref terminal, 1, 0) == 0) completed();
        }
        private void Fail(PostgresException exception)
        {
            if (Interlocked.CompareExchange(ref terminal, 2, 0) != 0) return;
            try { failed(exception); }
            catch { exception.Data["OwnerSqlEvidenceCaptureFailed"] = true; }
        }
        public override bool Read()
        { try { return inner.Read(); } catch (PostgresException error) { Fail(error); throw; } }
        public override async Task<bool> ReadAsync(CancellationToken token)
        { try { return await inner.ReadAsync(token).ConfigureAwait(false); } catch (PostgresException error) { Fail(error); throw; } }
        public override bool NextResult()
        { try { var next=inner.NextResult(); if (!next) Complete(); return next; } catch (PostgresException error) { Fail(error); throw; } }
        public override async Task<bool> NextResultAsync(CancellationToken token)
        { try { var next=await inner.NextResultAsync(token).ConfigureAwait(false); if (!next) Complete(); return next; } catch (PostgresException error) { Fail(error); throw; } }
        public override void Close()
        { try { inner.Close(); } catch (PostgresException error) { Fail(error); throw; } }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { inner.Dispose(); } catch (PostgresException error) { Fail(error); throw; } }
            base.Dispose(false);
        }
        public override async ValueTask DisposeAsync()
        { try { await inner.DisposeAsync().ConfigureAwait(false); } catch (PostgresException error) { Fail(error); throw; } }
        public override System.Collections.IEnumerator GetEnumerator() => new DbEnumerator(this, false);
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override int VisibleFieldCount => inner.VisibleFieldCount;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override bool GetBoolean(int i) => inner.GetBoolean(i);
        public override byte GetByte(int i) => inner.GetByte(i);
        public override long GetBytes(int i,long offset,byte[]? buffer,int start,int count) => inner.GetBytes(i,offset,buffer,start,count);
        public override char GetChar(int i) => inner.GetChar(i);
        public override long GetChars(int i,long offset,char[]? buffer,int start,int count) => inner.GetChars(i,offset,buffer,start,count);
        public override string GetDataTypeName(int i) => inner.GetDataTypeName(i);
        public override DateTime GetDateTime(int i) => inner.GetDateTime(i);
        public override decimal GetDecimal(int i) => inner.GetDecimal(i);
        public override double GetDouble(int i) => inner.GetDouble(i);
        public override Type GetFieldType(int i) => inner.GetFieldType(i);
        public override float GetFloat(int i) => inner.GetFloat(i);
        public override Guid GetGuid(int i) => inner.GetGuid(i);
        public override short GetInt16(int i) => inner.GetInt16(i);
        public override int GetInt32(int i) => inner.GetInt32(i);
        public override long GetInt64(int i) => inner.GetInt64(i);
        public override string GetName(int i) => inner.GetName(i);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int i) => inner.GetString(i);
        public override object GetValue(int i) => inner.GetValue(i);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int i) => inner.IsDBNull(i);
        public override T GetFieldValue<T>(int i) => inner.GetFieldValue<T>(i);
        public override Task<T> GetFieldValueAsync<T>(int i,CancellationToken token) => inner.GetFieldValueAsync<T>(i,token);
        public override Task<bool> IsDBNullAsync(int i,CancellationToken token) => inner.IsDBNullAsync(i,token);
        public override System.Data.DataTable? GetSchemaTable() => inner.GetSchemaTable();
    }
    private static void PreserveFailure(Observation? observation, PostgresException exception) =>
        PreserveTerminal(observation, exception, "postgres_error", exception.SqlState);

    private static void PreserveTerminal(Observation? observation, Exception exception, string outcome, string? state = null)
    {
        try { observation?.Write(outcome, state); }
        catch
        {
            // A collector failure must not replace the original execution failure.
            // The missing/malformed receipt still fails the coverage comparator closed.
            exception.Data["OwnerSqlEvidenceCaptureFailed"] = true;
        }
    }

    private static Observation? Prepare(DbCommand command, string file, int line)
    {
        if (!IsEnabled) return null;
        if (command.Connection is not NpgsqlConnection connection || connection.State != System.Data.ConnectionState.Open
            || connection.PostgreSqlVersion.Major != 17)
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_REQUIRES_POSTGRESQL_17");
        var sourceRoot = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_SOURCE_ROOT");
        var outputRoot = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(outputRoot) || line < 1)
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_CONFIGURATION_MISSING");
        sourceRoot = Path.GetFullPath(sourceRoot);
        outputRoot = Path.GetFullPath(outputRoot);
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(outputRoot)
            || (File.GetAttributes(sourceRoot) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(outputRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_ROOT_REJECTED");

        // CallerFilePath can come from a Windows build observed on Linux. Only these producer
        // boundaries may be translated into the independently supplied, current source checkout.
        var normalized = file.Replace('\\', '/');
        var anchors = new[] { "Tools/", "Shared/", "Services/AuthManager/AuthManager.Infrastructure/Persistence/" };
        var authSources = new[]
        {
            "Services/AuthManager/AuthManager.Infrastructure/Identity/BrowserSessions.cs",
            "Services/AuthManager/AuthManager.Infrastructure/Workers/WorkerIdentitySource.cs",
            "Services/AuthManager/AuthManager.Infrastructure/Workers/WorkerRotationStore.cs"
        };
        var candidates = anchors.Select(anchor => normalized.IndexOf("/" + anchor, StringComparison.Ordinal))
            .Where(index => index >= 0).Select(index => normalized[(index + 1)..])
            .Concat(authSources.Where(source => normalized.EndsWith("/" + source, StringComparison.Ordinal)))
            .Distinct().ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_PRODUCER_REJECTED");
        var relative = candidates[0];
        if (relative.Split('/').Any(part => part is ".." or "." or ""))
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_PRODUCER_REJECTED");
        var path = Path.GetFullPath(Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(sourceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_PRODUCER_REJECTED");
        var digest = SHA256.HashData(File.ReadAllBytes(path));
        VerifyCompiledDocument(normalized, digest);
        var hash = Convert.ToHexString(digest).ToLowerInvariant();
        // Exact invocation text digest only: secret-bearing CREATE ROLE statements have
        // high-entropy supplied literals, so this is not a canonical SQL-shape identifier.
        var queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText))).ToLowerInvariant();
        return new Observation(relative + ":L" + line, hash, queryHash, outputRoot);
    }

    private static void VerifyCompiledDocument(string callerPath, byte[] digest)
    {
        // The current checkout alone cannot identify the source used by a stale executable.
        // Portable-PDB document checksums bind each observation to the actual compiled producer.
        var pdbPath = Path.ChangeExtension(typeof(OwnerSqlExecution).Assembly.Location, ".pdb");
        if (!File.Exists(pdbPath)) throw new InvalidOperationException("OWNER_SQL_EVIDENCE_SYMBOLS_MISSING");
        using var stream = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        var matches = reader.Documents.Select(handle => reader.GetDocument(handle))
            .Where(document => string.Equals(reader.GetString(document.Name).Replace('\\', '/'), callerPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .ToArray();
        var sha256 = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");
        if (matches.Length != 1 || reader.GetGuid(matches[0].HashAlgorithm) != sha256
            || !CryptographicOperations.FixedTimeEquals(reader.GetBlobBytes(matches[0].Hash), digest))
            throw new InvalidOperationException("OWNER_SQL_EVIDENCE_COMPILED_SOURCE_MISMATCH");
    }

    private sealed record Observation(string Id, string Hash, string QueryHash, string OutputRoot)
    {
        internal void Write(string outcome, string? state = null)
        {
            var cancelled = outcome == "cancelled";
            var receipt = new
            {
                schema = cancelled ? "zeka-owner-sql-execution/v2" : "zeka-owner-sql-execution/v1",
                producerId = Id, producerSha256 = Hash, querySha256 = QueryHash,
                serverMajor = 17, executionCompleted = !cancelled, outcome, sqlState = state,
                processId = Environment.ProcessId, observedAtUtc = DateTimeOffset.UtcNow
            };
            lock (WriteLock)
            {
                File.AppendAllText(Path.Combine(OutputRoot, "owner-sql-" + Environment.ProcessId + "-"
                        + typeof(OwnerSqlExecution).Assembly.GetName().Name + ".jsonl"),
                    JsonSerializer.Serialize(receipt) + "\n");
            }
        }
    }
}
