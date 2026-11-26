using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Zeka.OwnerSqlEvidence;
using Zeka.ToolingDiagnostics;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    var capture = new SafePostgresDiagnosticCapture();
    var stage = "arguments";
    string? queryId = null;
    string? receiptFile = null;
    try
    {
        var allowed = new[] { "--connection-file", "--query-file", "--query-id", "--receipt-file" };
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 == arguments.Length || !allowed.Contains(arguments[index], StringComparer.Ordinal)
                || !options.TryAdd(arguments[index], arguments[index + 1]))
                throw new InvalidOperationException("OWNER_QUERY_ARGUMENTS_REJECTED");
        }
        if (options.Count != 4 || options.Values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("OWNER_QUERY_ARGUMENTS_REJECTED");
        receiptFile = options["--receipt-file"];
        queryId = options["--query-id"];
        if (queryId.Length > 128 || queryId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
            throw new InvalidOperationException("OWNER_QUERY_ID_REJECTED");
        var sql = await File.ReadAllTextAsync(options["--query-file"], Encoding.UTF8);
        RequireSingleReadOnlyQuery(sql);
        var connectionString = SafePostgresDiagnostic.WithoutErrorDetail(
            await File.ReadAllTextAsync(options["--connection-file"], Encoding.UTF8));
        capture.RegisterConnection(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        { Timeout = 10, CommandTimeout = 10, Pooling = false, IncludeErrorDetail = false };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        stage = "connection";
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(deadline.Token);
        if (connection.PostgreSqlVersion.Major != 17)
            throw new InvalidOperationException("OWNER_QUERY_REQUIRES_POSTGRESQL_17");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, deadline.Token);
        stage = "readonly-admission";
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await OwnerSqlExecution.ExecuteNonQueryAsync(readOnly, deadline.Token);
        stage = "query";
        JsonElement result;
        await using (var command = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 10 })
        await using (var reader = await OwnerSqlExecution.ExecuteReaderAsync(command, deadline.Token))
        {
            if (reader.FieldCount != 1 || !await reader.ReadAsync(deadline.Token))
                throw new InvalidOperationException("OWNER_QUERY_RESULT_SHAPE_REJECTED");
            using var document = JsonDocument.Parse(reader.GetString(0));
            if (document.RootElement.ValueKind != JsonValueKind.Object || await reader.ReadAsync(deadline.Token)
                || await reader.NextResultAsync(deadline.Token))
                throw new InvalidOperationException("OWNER_QUERY_RESULT_SHAPE_REJECTED");
            result = document.RootElement.Clone();
        }
        await transaction.RollbackAsync(deadline.Token);
        var receipt = new
        {
            schema = "zeka-owner-sql-probe/v1", status = "PASS", queryId, stage = "completed",
            queryExecuted = true, serverMajor = connection.PostgreSqlVersion.Major,
            readOnly = true, rolledBack = true, result, postgres = (SafePostgresDiagnostic?)null
        };
        await WriteReceiptAsync(options["--receipt-file"], receipt);
        Console.Out.WriteLine(JsonSerializer.Serialize(receipt));
        return 0;
    }
    catch (Exception error)
    {
        capture.Capture(error);
        var receipt = new
        {
            schema = "zeka-owner-sql-probe/v1", status = "FAIL", queryId, stage,
            code = capture.Diagnostic is null ? "OWNER_QUERY_REJECTED" : "OWNER_POSTGRES_QUERY_FAILED",
            postgres = capture.Diagnostic
        };
        if (receiptFile is not null)
        {
            try { await WriteReceiptAsync(receiptFile, receipt); }
            catch (IOException) { /* Preserve the first failure and safe stdout receipt. */ }
            catch (UnauthorizedAccessException) { /* Preserve the first failure. */ }
        }
        // Never serialize Exception.Message/ToString, Detail, context, query or connection data.
        Console.Out.WriteLine(JsonSerializer.Serialize(receipt));
        return capture.Diagnostic is null ? 2 : 3;
    }
}

static async Task WriteReceiptAsync<T>(string path, T value)
{
    await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    await JsonSerializer.SerializeAsync(stream, value);
}

// The native bridge accepts one SELECT/WITH query, not transaction-control or mutation scripts.
// Transaction templates and bootstrap batches are tested by the separate disposable corpus runner.
static void RequireSingleReadOnlyQuery(string sql)
{
    var tokens = new List<string>();
    var index = 0;
    var ended = false;
    while (index < sql.Length)
    {
        var c = sql[index];
        if (char.IsWhiteSpace(c)) { index++; continue; }
        if (c == '-' && index + 1 < sql.Length && sql[index + 1] == '-')
        { index += 2; while (index < sql.Length && sql[index] != '\n') index++; continue; }
        if (c == '/' && index + 1 < sql.Length && sql[index + 1] == '*')
        {
            var depth = 1; index += 2;
            while (index < sql.Length && depth > 0)
            {
                if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*') { depth++; index += 2; }
                else if (index + 1 < sql.Length && sql[index] == '*' && sql[index + 1] == '/') { depth--; index += 2; }
                else index++;
            }
            if (depth != 0) throw new InvalidOperationException("OWNER_QUERY_TEXT_REJECTED");
            continue;
        }
        if (ended) throw new InvalidOperationException("OWNER_QUERY_MULTIPLE_STATEMENTS_REJECTED");
        if (c == ';') { ended = true; index++; continue; }
        if (c is '\'' or '"')
        {
            var quote = c; index++; var closed = false;
            while (index < sql.Length)
            {
                if (sql[index++] != quote) continue;
                if (index < sql.Length && sql[index] == quote) { index++; continue; }
                closed = true; break;
            }
            if (!closed) throw new InvalidOperationException("OWNER_QUERY_TEXT_REJECTED");
            continue;
        }
        if (c == '$') throw new InvalidOperationException("OWNER_QUERY_DOLLAR_TEXT_REJECTED");
        if (char.IsAsciiLetter(c) || c == '_')
        {
            var begin = index++;
            while (index < sql.Length && (char.IsAsciiLetterOrDigit(sql[index]) || sql[index] == '_')) index++;
            tokens.Add(sql[begin..index].ToUpperInvariant());
        }
        else index++;
    }
    var forbidden = new[] { "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP",
        "GRANT", "REVOKE", "COPY", "CALL", "DO", "SET", "RESET", "COMMIT", "ROLLBACK" };
    if (tokens.Count == 0 || tokens[0] is not ("SELECT" or "WITH") || tokens.Any(forbidden.Contains))
        throw new InvalidOperationException("OWNER_QUERY_NOT_READONLY_SELECT");
}
