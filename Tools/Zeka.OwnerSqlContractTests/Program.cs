using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.Versioning;
using Npgsql;
using Zeka.ToolingDiagnostics;
[assembly: SupportedOSPlatform("linux")]

// A destructive SYNTHETIC disposable-cluster runner, never a protected M1 admission.
// No source-only receipt can mark a producer covered: only the executed CLI evidence does that.
return await Corpus.MainAsync(args);

internal static class Corpus
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    private static readonly List<object> Results = [];
    private static readonly Dictionary<string, string> RuntimePasswords = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> MigratorPasswords = new(StringComparer.Ordinal);
    private static string AdminPassword = "";
    private static string EvidenceRoot = "";
    private static string ActiveCase = "input";
    private static readonly SafePostgresDiagnosticCapture FailureCapture = new();
    private static string Probe = "";
    private static string Dotnet = "";
    private static string Setup = "";
    private static string Migrator = "";
    private static string Owner = "";
    private static string SourceRoot = "";
    private static string Socket = "";
    private static string FixtureId = "";
    private static string InitializationId = "";
    private static string SourceCommit = "";
    private static Guid SubjectId;
    private static string IdempotencyKey = "";

    public static async Task<int> MainAsync(string[] args)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        try
        {
            var options = Parse(args);
            Dotnet = RequiredFile(options, "dotnet"); Setup = RequiredFile(options, "setup");
            Migrator = RequiredFile(options, "migrator"); Owner = RequiredFile(options, "owner");
            if (options.ContainsKey("probe")) Probe = RequiredFile(options, "probe");
            SourceRoot = Path.GetFullPath(options["source-root"]);
            Socket = options["fixture-host"];
            Require(OperatingSystem.IsLinux() && Path.IsPathFullyQualified(Socket) && options["fixture-port"] == "5432", "LINUX_LOCAL_FIXTURE_REQUIRED");
            EvidenceRoot = Path.GetFullPath(options["evidence-root"]);
            Require(EvidenceRoot.StartsWith("/tmp/", StringComparison.Ordinal) && !Path.Exists(EvidenceRoot), "FRESH_FIXTURE_EVIDENCE_REQUIRED");
            Directory.CreateDirectory(EvidenceRoot, PrivateDirectory);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(RequiredFile(options, "case-file"), limit.Token));
            var input = document.RootElement;
            Require(input.GetProperty("schema").GetString() == "zeka-owner-sql-corpus/v1", "CASE_SCHEMA_REJECTED");
            FixtureId = input.GetProperty("fixtureId").GetString()!;
            InitializationId = input.GetProperty("initializationId").GetString()!;
            SourceCommit = input.GetProperty("sourceCommit").GetString()!;
            SubjectId = input.GetProperty("subjectId").GetGuid();
            IdempotencyKey = input.GetProperty("idempotencyKey").GetString()!;
            Require(Regex.IsMatch(FixtureId, "\\Azeka-m1-bootstrap-fixture-[0-9a-f]{32}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
                && Guid.TryParseExact(InitializationId, "D", out var initialization) && initialization != Guid.Empty
                && SourceCommit.Length == 40 && SourceCommit.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && SubjectId != Guid.Empty && IdempotencyKey.Length is > 0 and <= 200,
                "SYNTHETIC_CASE_BINDING_REJECTED");
            Require(Path.Exists(Path.Combine(SourceRoot, "Deployments/database/bootstrap-auth-roles.sql")), "SOURCE_ROOT_REJECTED");
            var hosts = await Dns.GetHostAddressesAsync("postgres", limit.Token);
            Require(hosts.Length > 0 && hosts.All(IPAddress.IsLoopback), "FIXTURE_TCP_HOST_NOT_LOOPBACK");
            await AdmitFreshFixtureAsync(limit.Token);
            await PrepareFixedPathsAsync(limit.Token);
            ActiveCase = "setup.guard-checks";
            await InvokeAsync(Setup, ["--guard-checks"], null, 0, limit.Token, new() { ["ZEKA_M1_GUARD_FIXTURE"] = FixtureId });
            Require(File.Exists("/run/results/guard-checks.json"), "GUARD_RESULT_MISSING");
            using (var guard = JsonDocument.Parse(await File.ReadAllTextAsync("/run/results/guard-checks.json", limit.Token)))
                Require(guard.RootElement.GetProperty("status").GetString() == "passed", "GUARD_RESULT_NOT_PASSED");
            foreach (var service in new[] { "auth", "adminarea", "client" })
                await BootstrapServiceAsync(service, limit.Token);
            // Read-only query corpus executes before synthetic first-admin DML.
            await ExecuteQueriesAsync(input.GetProperty("queries"), input.GetProperty("transactions"), limit.Token);
            await ExecuteRlsMetadataCasesAsync(input.GetProperty("queries"), limit.Token);

            await ExecuteOwnerCasesAsync(limit.Token);
            if (Probe.Length > 0) await ExecuteDiagnosticCasesAsync(input.GetProperty("queries")[0].GetProperty("sql").GetString()!, limit.Token);
            await WriteReportAsync("PASS", null, null, limit.Token);
            return 0;
        }
        catch (Exception error)
        {
            var code = error is CorpusFailure failure ? failure.Code : error is OperationCanceledException ? "CORPUS_DEADLINE" : "CORPUS_FAILED";
            FailureCapture.Capture(error);
            var sqlState = FailureCapture.Diagnostic?.SqlState;
            if (EvidenceRoot.Length > 0)
                await WriteReportAsync("FAIL", code, sqlState, CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", caseId = ActiveCase, code, sqlState, exceptionType = error.GetType().Name }));
            return 1;
        }
    }

    private static async Task AdmitFreshFixtureAsync(CancellationToken ct)
    {
        ActiveCase = "fixture.admission";
        await using var c = new NpgsqlConnection(AdminConnection("postgres")); await c.OpenAsync(ct);
        const string sql = """
            SELECT current_database()='postgres' AND session_user='postgres' AND current_user='postgres'
              AND inet_server_addr() IS NULL AND current_setting('server_version_num')::int BETWEEN 170000 AND 179999
              AND current_setting('log_statement')='none' AND current_setting('log_min_error_statement')='panic'
              AND (SELECT rolsuper FROM pg_roles WHERE rolname=current_user)
              AND NOT EXISTS(SELECT 1 FROM pg_database WHERE datname NOT IN ('postgres','template0','template1'))
              AND NOT EXISTS(SELECT 1 FROM pg_roles WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_')
              AND (SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname='postgres')=@fixture
            """;
        await using var cmd = new NpgsqlCommand(sql, c); cmd.Parameters.AddWithValue("fixture", FixtureId);
        Require(await cmd.ExecuteScalarAsync(ct) is true, "FRESH_DISPOSABLE_CLUSTER_NOT_ADMITTED");
        AdminPassword = FreshPassword();
        // Only after the fixture/logging admission; generated password never leaves private memory/files.
        await ExecuteAsync(c, "ALTER ROLE postgres PASSWORD " + Literal(AdminPassword), ct);
        Results.Add(new { id = ActiveCase, status = "PASS", server = "PostgreSQL17", transport = "local-unix", protectedM1 = "NOT_USED" });
    }

    private static async Task PrepareFixedPathsAsync(CancellationToken ct)
    {
        foreach (var path in new[] { "/run/setup", "/run/secrets", "/run/source", "/run/results" })
        {
            Require(!Path.Exists(path), "FIXED_FIXTURE_PATH_ALREADY_EXISTS");
            Directory.CreateDirectory(path, PrivateDirectory);
        }
        await WritePrivateAsync("/run/secrets/admin-password", AdminPassword, ct);
        await WritePrivateAsync("/run/secrets/guard-fixture-marker", FixtureId, ct);
        foreach (var service in new[] { "auth", "adminarea", "client" })
            File.Copy(Path.Combine(SourceRoot, "Deployments/database", "bootstrap-" + service + "-roles.sql"), "/run/source/bootstrap-" + service + "-roles.sql");
        var setupDirectory = Path.GetDirectoryName(Setup)!;
        Require(File.Exists(Path.Combine(setupDirectory, "zeka-db-migrate.dll")), "SETUP_COLOCATED_MIGRATOR_MISSING");
        // The production adapter invokes `dotnet` by name, with fixed relative setup deps/runtimeconfig.
        Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(Dotnet) + ":" + Environment.GetEnvironmentVariable("PATH"));
    }

    private static async Task BootstrapServiceAsync(string service, CancellationToken ct)
    {
        ActiveCase = "setup." + service + ".initialize";
        var runtime = FreshPassword(); var migrator = FreshPassword();
        RuntimePasswords.Add(service, runtime); MigratorPasswords.Add(service, migrator);
        await WritePrivateAsync("/run/secrets/runtime-password", runtime, ct);
        await WritePrivateAsync("/run/secrets/migrator-password", migrator, ct);
        var key = service == "auth" ? "Default" : "ClientApiConnection";
        // These names are the exact Service.For configuration contracts, validated by the production CLI.
        var settings = JsonSerializer.Serialize(new { ConnectionStrings = new Dictionary<string, string> { [key] = RuntimeConnection(service) } });
        await WritePrivateAsync("/run/secrets/runtime-config.json", settings, ct);
        var request = new Dictionary<string, object?> { ["Project"] = "zeka-m1", ["Service"] = service,
            ["InitializationId"] = InitializationId, ["SourceCommit"] = SourceCommit, ["Completed"] = false,
            ["ClusterId"] = null, ["SchemaSha256"] = null, ["HistorySha256"] = null };
        await WritePrivateAsync("/run/setup/request.json", JsonSerializer.Serialize(request), ct);
        await InvokeAsync(Setup, [], null, 0, ct);
        var resultBytes = await File.ReadAllBytesAsync("/run/results/result.json", ct);
        using var result = JsonDocument.Parse(resultBytes);
        Require(result.RootElement.GetProperty("status").GetString() == "INITIALIZED", "SETUP_INITIAL_RESULT_REJECTED");
        File.Copy("/run/results/result.json", Path.Combine(EvidenceRoot, service + "-initialized.json"));
        request["Completed"] = true;
        foreach (var name in new[] { "clusterId", "schemaSha256", "historySha256" })
            request[char.ToUpperInvariant(name[0]) + name[1..]] = result.RootElement.GetProperty(name).GetString();
        await WritePrivateAsync("/run/setup/request.json", JsonSerializer.Serialize(request), ct);
        ActiveCase = "setup." + service + ".repeat";
        await InvokeAsync(Setup, [], null, 0, ct);
        using var repeat = JsonDocument.Parse(await File.ReadAllTextAsync("/run/results/result.json", ct));
        Require(repeat.RootElement.GetProperty("status").GetString() == "ALREADY_CURRENT", "SETUP_REPEAT_RESULT_REJECTED");
        File.Copy("/run/results/result.json", Path.Combine(EvidenceRoot, service + "-repeat.json"));
        // Direct actual CLI plan covers the separately published tool as well as setup's native adapter.
        await InvokeMigrationAsync(service, "plan", "latest", 0, ct);
    }

    private static async Task ExecuteQueriesAsync(JsonElement queries, JsonElement transactions, CancellationToken ct)
    {
        Require(queries.ValueKind == JsonValueKind.Array && queries.GetArrayLength() >= 8, "QUERY_CORPUS_INCOMPLETE");
        var transactionText = new[] { "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY", "SET LOCAL statement_timeout='10000ms'", "SET LOCAL lock_timeout='1000ms'", "ROLLBACK" };
        var transactionIds = new[] { "owner.transaction.begin-readonly.v1", "owner.transaction.statement-timeout.v1", "owner.transaction.lock-timeout.v1", "owner.transaction.rollback.v1" };
        Require(transactions.ValueKind == JsonValueKind.Array && transactions.GetArrayLength() == 4, "TRANSACTION_CORPUS_INCOMPLETE");
        for (var i = 0; i < 4; i++)
            Require(transactions[i].GetProperty("id").GetString() == transactionIds[i] && transactions[i].GetProperty("sql").GetString() == transactionText[i], "TRANSACTION_CORPUS_REJECTED");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var query in queries.EnumerateArray())
        {
            var id = query.GetProperty("id").GetString()!;
            Require(Regex.IsMatch(id, "\\A[a-z0-9][a-z0-9.-]{0,120}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && ids.Add(id), "QUERY_CASE_ID_REJECTED");
            ActiveCase = id;
            var producerId = query.GetProperty("producerId").GetString()!;
            var sourcePath = query.GetProperty("sourcePath").GetString()!;
            var sourceSha256 = query.GetProperty("sourceSha256").GetString()!;
            await ValidateSourceBindingAsync(sourcePath, sourceSha256, ct);
            var service = query.GetProperty("service").GetString()!;
            Require(RuntimePasswords.ContainsKey(service), "QUERY_SERVICE_REJECTED");
            var sql = query.GetProperty("sql").GetString()!;
            Require(sql.Length is > 0 and <= 1048576 && !sql.Contains('\0'), "QUERY_TEXT_REJECTED");
            await using var c = new NpgsqlConnection(RuntimeConnection(service)); await c.OpenAsync(ct);
            for (var i = 0; i < 4; i++)
                await ValidateSourceBindingAsync(transactions[i].GetProperty("sourcePath").GetString()!, transactions[i].GetProperty("sourceSha256").GetString()!, ct);
            for (var i = 0; i < 3; i++)
            {
                await ExecuteAsync(c, transactionText[i], ct);
                await RecordSqlExecutionAsync(transactionIds[i], transactions[i].GetProperty("sourcePath").GetString()!,
                    transactions[i].GetProperty("sourceSha256").GetString()!, c.PostgreSqlVersion.Major, null, Digest(Encoding.UTF8.GetBytes(transactionText[i])), ct);
            }
            string? state = null;
            try
            {
                await using var command = new NpgsqlCommand(sql, c) { CommandTimeout = 15 };
                await using var reader = await command.ExecuteReaderAsync(ct);
                var rows = 0;
                while (await reader.ReadAsync(ct))
                {
                    rows++;
                    if (query.TryGetProperty("expect", out var expectedFacts))
                    {
                        Require(rows == 1 && reader.FieldCount == 1, "QUERY_OBSERVATION_SHAPE_REJECTED");
                        using var observed = JsonDocument.Parse(reader.GetString(0));
                        foreach (var fact in expectedFacts.EnumerateObject())
                            Require(observed.RootElement.TryGetProperty(fact.Name, out var actual) && actual.ValueKind == fact.Value.ValueKind
                                && actual.ToString() == fact.Value.ToString(), "QUERY_OBSERVATION_REJECTED");
                    }
                }
                Require(rows > 0, "QUERY_RETURNED_NO_OBSERVATION");
            }
            catch (PostgresException error) { state = error.SqlState; }
            finally
            {
                await ExecuteAsync(c, transactionText[3], ct);
                await RecordSqlExecutionAsync(transactionIds[3], transactions[3].GetProperty("sourcePath").GetString()!,
                    transactions[3].GetProperty("sourceSha256").GetString()!, c.PostgreSqlVersion.Major, null, Digest(Encoding.UTF8.GetBytes(transactionText[3])), ct);
            }
            await RecordSqlExecutionAsync(producerId, sourcePath, sourceSha256, c.PostgreSqlVersion.Major, state, Digest(Encoding.UTF8.GetBytes(sql)), ct);
            var expected = query.TryGetProperty("expectedSqlState", out var expectedState) ? expectedState.GetString() : null;
            Require(state == expected, "QUERY_SQLSTATE_UNEXPECTED");
            var queryHash = Digest(Encoding.UTF8.GetBytes(sql));
            var receipt = new { id, producerId, sourcePath, sourceSha256, status = "PASS", sqlState = state, querySha256 = queryHash,
                readOnly = true, observation = query.TryGetProperty("expect", out _) ? "ASSERTED" : "EXECUTION_ONLY_NOT_ADMISSION" };
            Results.Add(receipt);
            await WritePrivateAsync(Path.Combine(EvidenceRoot, id + ".json"), JsonSerializer.Serialize(receipt, Json), ct);
            for (var i = 0; i < 4; i++)
            {
                await ValidateSourceBindingAsync(transactions[i].GetProperty("sourcePath").GetString()!, transactions[i].GetProperty("sourceSha256").GetString()!, ct);
                Results.Add(new { id = transactionIds[i], producerId = transactions[i].GetProperty("producerId").GetString(),
                    sourcePath = transactions[i].GetProperty("sourcePath").GetString(), sourceSha256 = transactions[i].GetProperty("sourceSha256").GetString(),
                    querySha256 = Digest(Encoding.UTF8.GetBytes(transactionText[i])), status = "PASS", executedForCase = id });
            }
        }
    }

    private static async Task RecordSqlExecutionAsync(string producerId, string sourcePath, string sourceHash,
        int serverMajor, string? sqlState, string querySha256, CancellationToken ct)
    {
        const string factory = "Deployments/local-stack/OwnerFirstAdminPreflightQueries.ps1";
        Require(sourcePath == factory && serverMajor == 17, "QUERY_RECEIPT_BINDING_REJECTED");
        var allowed = new[] { "owner.identity.v1", "owner.schema-presence.v1", "owner.schema-shape.v1", "owner.privileges.v1",
            "owner.catalogue.v1", "owner.existing-identity.v1", "owner.idempotency.v1", "owner.rls-metadata.v1",
            "owner.transaction.begin-readonly.v1", "owner.transaction.statement-timeout.v1", "owner.transaction.lock-timeout.v1", "owner.transaction.rollback.v1" };
        Require(allowed.Contains(producerId, StringComparer.Ordinal), "QUERY_PRODUCER_ID_REJECTED");
        await ValidateSourceBindingAsync(sourcePath, sourceHash, ct);
        var bindings = new Dictionary<string,string>(StringComparer.Ordinal);
        var delegated = producerId == "owner.privileges.v1" ? "Deployments/local-stack/OwnerFirstAdminPreflightPrivileges.ps1"
            : producerId == "owner.rls-metadata.v1" ? "Deployments/local-stack/OwnerFirstAdminRlsMetadata.ps1" : null;
        if (delegated is not null) bindings[delegated] = Digest(await File.ReadAllBytesAsync(Path.Combine(SourceRoot, delegated), ct));
        var receipt = new { schema = "zeka-owner-sql-execution/v1", producerId, producerSha256 = sourceHash,
            sourceBindings = bindings, querySha256, serverMajor, executionCompleted = true,
            outcome = sqlState is null ? "success" : "postgres_error", sqlState, processId = Environment.ProcessId,
            observedAtUtc = DateTimeOffset.UtcNow.ToString("O") };
        var directory = Environment.GetEnvironmentVariable("ZEKA_OWNER_SQL_EVIDENCE_DIR") ?? EvidenceRoot;
        Require(Path.IsPathFullyQualified(directory) && directory.StartsWith("/tmp/", StringComparison.Ordinal), "SQL_RECEIPT_PATH_REJECTED");
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, PrivateDirectory);
        Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "SQL_RECEIPT_PATH_REJECTED");
        var path = Path.Combine(directory, "owner-sql-" + Environment.ProcessId + ".jsonl");
        await using var stream = new FileStream(path,new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write,
            Share = FileShare.Read, UnixCreateMode = PrivateFile });
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt) + "\n");
        await stream.WriteAsync(bytes,ct); await stream.FlushAsync(ct);
    }
    private static async Task ValidateSourceBindingAsync(string relativePath, string expectedHash, CancellationToken ct)
    {
        Require(!Path.IsPathFullyQualified(relativePath) && !relativePath.Contains('\\') && !relativePath.Split('/').Contains(".."), "QUERY_SOURCE_PATH_REJECTED");
        var path = Path.GetFullPath(Path.Combine(SourceRoot, relativePath));
        Require(path.StartsWith(SourceRoot.TrimEnd('/') + "/", StringComparison.Ordinal) && File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0, "QUERY_SOURCE_PATH_REJECTED");
        Require(Digest(await File.ReadAllBytesAsync(path, ct)) == expectedHash, "QUERY_SOURCE_HASH_REJECTED");
    }
    private static async Task ExecuteRlsMetadataCasesAsync(JsonElement queries, CancellationToken ct)
    {
        var cases = queries.EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("sql").GetString()!, StringComparer.Ordinal);
        var sql = cases["owner.rls-metadata.v1"];
        const string table = "public.\"AuthClosureParticipantInbox\"";
        string name, qual, check;
        await using var admin = new NpgsqlConnection(AdminConnection("zeka_m1_auth"));
        await admin.OpenAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT policyname,qual,with_check FROM pg_policies WHERE schemaname='public' AND tablename='AuthClosureParticipantInbox'", admin))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            Require(await reader.ReadAsync(ct), "RLS_FIXTURE_POLICY_MISSING");
            name = "\"" + reader.GetString(0).Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            qual = reader.GetString(1); check = reader.GetString(2);
            Require(!await reader.ReadAsync(ct), "RLS_FIXTURE_POLICY_AMBIGUOUS");
        }
        var policy = name + " ON " + table;
        var create = "CREATE POLICY " + policy + " AS PERMISSIVE FOR ALL TO zeka_auth_owner,zeka_auth_runtime USING (" + qual + ") WITH CHECK (" + check + ")";
        await ObserveMetadataAsync("rls.baseline-exclusions", true);
        foreach (var roles in new[] { "zeka_auth_runtime,zeka_auth_owner", "zeka_auth_owner,zeka_auth_runtime" })
        {
            await ExecuteAsync(admin, "ALTER POLICY " + policy + " TO " + roles, ct);
            await ObserveMetadataAsync("rls.role-order-" + (roles.StartsWith("zeka_auth_runtime", StringComparison.Ordinal) ? "runtime-first" : "owner-first"), true);
        }
        // PostgreSQL retains repeated role OIDs. Exact-role admission must reject duplicates.
        ActiveCase = "rls.duplicate-roles-refused";
        await ExecuteAsync(admin, "ALTER POLICY " + policy + " TO zeka_auth_owner,zeka_auth_runtime,zeka_auth_runtime", ct);
        await using (var command = new NpgsqlCommand("SELECT cardinality(polroles)=3 FROM pg_policy WHERE polrelid='public.\"AuthClosureParticipantInbox\"'::regclass", admin))
            Require(await command.ExecuteScalarAsync(ct) is true, "RLS_DUPLICATE_ROLE_CATALOG_REJECTED");
        await ObserveMetadataAsync("rls.duplicate-roles-refused", false);
        foreach (var roles in new[] { "PUBLIC", "zeka_auth_migrator" })
        {
            await ExecuteAsync(admin, "ALTER POLICY " + policy + " TO " + roles, ct);
            await ObserveMetadataAsync(roles == "PUBLIC" ? "rls.public-role-refused" : "rls.wrong-role-refused", false);
        }
        await ExecuteAsync(admin, "ALTER POLICY " + policy + " TO zeka_auth_owner,zeka_auth_runtime", ct);
        await ExecuteAsync(admin, "DROP POLICY " + policy, ct);
        await ObserveMetadataAsync("rls.missing-policy-refused", false);
        await ExecuteAsync(admin, create, ct);
        await ObserveMetadataAsync("rls.policy-restored-fixture-only", true);
        // Grants are a separate preflight gate; the metadata observer does not claim to check them.
        await ExecuteAsync(admin, "REVOKE SELECT ON public.\"PermissionSets\" FROM zeka_auth_runtime", ct);
        ActiveCase = "rls.separate-privilege-gate-refuses-missing-grant";
        var privilege = await ObserveAsync(cases["owner.privileges.v1"]);
        Require(!privilege.GetProperty("otherTablePrivilegesAllowed").GetBoolean(), "RLS_PRIVILEGE_NEGATIVE_NOT_REFUSED");
        Results.Add(new { id = ActiveCase, status = "PASS", realPostgres = true, metadataIsNotPrivilegeOrBehaviorProof = true });
        await ExecuteAsync(admin, "GRANT SELECT ON public.\"PermissionSets\" TO zeka_auth_runtime", ct);
        await ObserveMetadataAsync("rls.final-restored-fixture-only", true);

        async Task ObserveMetadataAsync(string id, bool allowed)
        {
            ActiveCase = id;
            var observed = await ObserveAsync(sql);
            Require(observed.GetProperty("metadataAdmission").GetBoolean() == allowed, "RLS_METADATA_EXPECTATION_REJECTED");
            Require(observed.GetProperty("sevenProvisioningTablesExplicitlyExcluded").GetBoolean(), "RLS_MANIFEST_EXCLUSIONS_REJECTED");
            if (!allowed) Require(observed.GetProperty("policyFailures").GetArrayLength() > 0, "RLS_POLICY_REFUSAL_NOT_OBSERVED");
            Results.Add(new { id, status = "PASS", realPostgres = true, metadataAdmission = allowed, behavioralTenantIsolation = "NOT_RUN" });
        }
        async Task<JsonElement> ObserveAsync(string query)
        {
            await using var connection = new NpgsqlConnection(RuntimeConnection("auth"));
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
            await ExecuteAsync(connection, "SET TRANSACTION READ ONLY", ct, transaction);
            JsonElement result;
            await using (var command = new NpgsqlCommand(query, connection, transaction) { CommandTimeout = 15 })
            await using (var reader = await command.ExecuteReaderAsync(ct))
            {
                Require(await reader.ReadAsync(ct) && reader.FieldCount == 1, "RLS_OBSERVATION_MISSING");
                using var document = JsonDocument.Parse(reader.GetString(0)); result = document.RootElement.Clone();
                Require(!await reader.ReadAsync(ct) && !await reader.NextResultAsync(ct), "RLS_OBSERVATION_AMBIGUOUS");
            }
            await transaction.RollbackAsync(ct);
            return result;
        }
    }
    private static async Task ExecuteDiagnosticCasesAsync(string validQuery, CancellationToken ct)
    {
        ActiveCase = "diagnostics.probe-valid-owner-query";
        await InvokeProbeAsync(validQuery, 0, null, ct);
        ActiveCase = "diagnostics.real-name-array-operator";
        var incompatible = await InvokeProbeAsync("SELECT json_build_object('mismatch', ARRAY['x'::name] = ARRAY['x'::text])", 3, null, ct);
        var mismatch = AssertFourFields(incompatible.GetProperty("postgres"));
        Require(mismatch.SqlState=="42883" && mismatch.Message.Contains("operator does not exist",StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(mismatch.Hint) && mismatch.Position>0,"REAL_COMPILER_DIAGNOSTIC_REJECTED");
        Results.Add(new { id=ActiveCase, status="PASS", postgres=mismatch, realPostgres=true });

        var detailCanary = "row_detail_" + FreshPassword();
        var rawConnection = new NpgsqlConnectionStringBuilder(AdminConnection("zeka_m1_auth")) { IncludeErrorDetail=true }.ConnectionString;
        await using var administrator = new NpgsqlConnection(rawConnection); await administrator.OpenAsync(ct);
        ActiveCase = "diagnostics.real-detail-control";
        // No writes are done through the probe. This fixture-only function just raises an error.
        var ddl = "CREATE FUNCTION public.owner_sql_detail_canary() RETURNS json LANGUAGE plpgsql AS $fixture$ BEGIN " +
            "RAISE EXCEPTION USING ERRCODE='23505',MESSAGE='Diagnostic fixture refusal',HINT='Safe fixture hint',DETAIL=" + Literal(detailCanary) + "; END $fixture$";
        await ExecuteAsync(administrator,ddl,ct);
        await ExecuteAsync(administrator,"GRANT EXECUTE ON FUNCTION public.owner_sql_detail_canary() TO zeka_auth_runtime",ct);
        PostgresException? detailError=null;
        try { await ExecuteAsync(administrator,"SELECT public.owner_sql_detail_canary()",ct); }
        catch(PostgresException error) { detailError=error; }
        Require(detailError?.SqlState=="23505" && detailError.Detail is not null && detailError.Detail.Contains(detailCanary,StringComparison.Ordinal),"REAL_DETAIL_CONTROL_NOT_OBSERVED");
        // Raw Detail is checked only in memory and never serialized or passed to a logger.
        ActiveCase = "diagnostics.probe-detail-dropped";
        var dropped = await InvokeProbeAsync("SELECT public.owner_sql_detail_canary()",3,detailCanary,ct);
        var projected = AssertFourFields(dropped.GetProperty("postgres"));
        Require(projected.SqlState=="23505" && projected.Message=="Diagnostic fixture refusal" && projected.Hint=="Safe fixture hint", "REAL_DETAIL_PROJECTION_REJECTED");
        Results.Add(new { id=ActiveCase,status="PASS",rawDetailCanaryObservedInMemory=true,detailAbsentFromStdoutAndReceipt=true,postgres=projected });

        ActiveCase = "diagnostics.real-unique-row-detail";
        await ExecuteAsync(administrator,"CREATE TABLE public.\"OwnerSqlDiagnosticUnique\" (\"Id\" integer PRIMARY KEY,\"Value\" text NOT NULL UNIQUE)",ct);
        var rowCanary = "unique_row_"+FreshPassword();
        PostgresException? uniqueError=null;
        await using(var insert=new NpgsqlCommand("INSERT INTO public.\"OwnerSqlDiagnosticUnique\" VALUES (1,@value),(2,@value)",administrator))
        {
            insert.Parameters.AddWithValue("value",rowCanary);
            try { await insert.ExecuteNonQueryAsync(ct); } catch(PostgresException error) { uniqueError=error; }
        }
        Require(uniqueError?.SqlState=="23505" && uniqueError.Detail is not null && uniqueError.Detail.Contains(rowCanary,StringComparison.Ordinal),"UNIQUE_ROW_DETAIL_NOT_OBSERVED");
        var safeUnique=SafePostgresDiagnostic.From(uniqueError);
        Require(safeUnique is not null,"UNIQUE_SAFE_DIAGNOSTIC_MISSING");
        var uniqueJson=JsonSerializer.Serialize(safeUnique);
        Require(!uniqueJson.Contains(rowCanary,StringComparison.Ordinal),"UNIQUE_ROW_DETAIL_LEAKED");
        using(var parsed=JsonDocument.Parse(uniqueJson)) AssertFourFields(parsed.RootElement);
        Results.Add(new {id=ActiveCase,status="PASS",rawDetailCanaryObservedInMemory=true,postgres=safeUnique});

        ActiveCase = "diagnostics.real-secret-message-redaction";
        var syntheticSecret="not_a_uuid_"+FreshPassword();
        var collector=new SafePostgresDiagnosticCapture(); collector.RegisterSecret(syntheticSecret);
        PostgresException? invalidError=null;
        await using(var invalid=new NpgsqlCommand("SELECT @value::uuid",administrator))
        {
            invalid.Parameters.AddWithValue("value",syntheticSecret);
            try { await invalid.ExecuteScalarAsync(ct); } catch(PostgresException error) { invalidError=error; collector.Capture(error); }
        }
        Require(invalidError?.SqlState=="22P02" && invalidError.MessageText.Contains(syntheticSecret,StringComparison.Ordinal),"REAL_SECRET_MESSAGE_CONTROL_NOT_OBSERVED");
        var safe=collector.Diagnostic; Require(safe is not null,"REAL_SECRET_DIAGNOSTIC_MISSING");
        var safeJson=JsonSerializer.Serialize(safe);
        Require(!safeJson.Contains(syntheticSecret,StringComparison.Ordinal) && safe!.Message.Contains("[REDACTED]",StringComparison.Ordinal),"REGISTERED_SECRET_NOT_REDACTED");
        using(var parsed=JsonDocument.Parse(safeJson)) AssertFourFields(parsed.RootElement);
        Results.Add(new {id=ActiveCase,status="PASS",realMessageContainedSyntheticSecretInMemory=true,postgres=safe});
        ActiveCase="diagnostics.real-secret-hint-redaction";
        await ExecuteAsync(administrator,"CREATE FUNCTION public.owner_sql_secret_hint(value text) RETURNS json LANGUAGE plpgsql AS $fixture$ BEGIN " +
            "RAISE EXCEPTION USING ERRCODE='P0001',MESSAGE='Fixture message '||value,HINT='Fixture hint '||value; END $fixture$",ct);
        var hintCollector=new SafePostgresDiagnosticCapture(); hintCollector.RegisterSecret(syntheticSecret);
        PostgresException? hintError=null;
        await using(var command=new NpgsqlCommand("SELECT public.owner_sql_secret_hint(@value)",administrator))
        {
            command.Parameters.AddWithValue("value",syntheticSecret);
            try { await command.ExecuteScalarAsync(ct); } catch(PostgresException error) { hintError=error; hintCollector.Capture(error); }
        }
        Require(hintError?.MessageText.Contains(syntheticSecret,StringComparison.Ordinal)==true && hintError.Hint?.Contains(syntheticSecret,StringComparison.Ordinal)==true,
            "REAL_SECRET_HINT_CONTROL_NOT_OBSERVED");
        var safeHint=hintCollector.Diagnostic;
        Require(safeHint is not null && safeHint.Message.Contains("[REDACTED]",StringComparison.Ordinal)
            && safeHint.Hint?.Contains("[REDACTED]",StringComparison.Ordinal)==true
            && !JsonSerializer.Serialize(safeHint).Contains(syntheticSecret,StringComparison.Ordinal),"REGISTERED_HINT_SECRET_NOT_REDACTED");
        Results.Add(new {id=ActiveCase,status="PASS",realMessageAndHintContainedSyntheticSecretInMemory=true,postgres=safeHint});
    }

    private static SafePostgresDiagnostic AssertFourFields(JsonElement value)
    {
        Require(value.ValueKind==JsonValueKind.Object,"FOUR_FIELD_DIAGNOSTIC_REQUIRED");
        var names=value.EnumerateObject().Select(p=>p.Name).Order(StringComparer.Ordinal).ToArray();
        Require(names.SequenceEqual(new[]{"hint","message","position","sqlState"},StringComparer.Ordinal),"FOUR_FIELD_DIAGNOSTIC_REQUIRED");
        return SafePostgresDiagnostic.Parse(value);
    }

    private static async Task<JsonElement> InvokeProbeAsync(string sql,int expectedExit,string? forbidden,CancellationToken ct)
    {
        var id="probe-"+Guid.NewGuid().ToString("N");
        var connectionFile="/run/secrets/probe-connection.private";
        var queryFile=Path.Combine(EvidenceRoot,id+".sql");
        var receiptFile=Path.Combine(EvidenceRoot,id+"-receipt.json");
        await WritePrivateAsync(connectionFile,RuntimeConnection("auth"),ct);
        await WritePrivateAsync(queryFile,sql,ct);
        await InvokeAsync(Probe,["--connection-file",connectionFile,"--query-file",queryFile,"--query-id",id,"--receipt-file",receiptFile],null,expectedExit,ct,
            null,expectedExit==0?null:"OWNER_POSTGRES_QUERY_FAILED",forbidden);
        var receipt=await File.ReadAllTextAsync(receiptFile,ct);
        if(forbidden is not null) Require(!receipt.Contains(forbidden,StringComparison.Ordinal),"PROBE_RECEIPT_DETAIL_LEAKED");
        using var document=JsonDocument.Parse(receipt);
        Require(document.RootElement.GetProperty("status").GetString()==(expectedExit==0?"PASS":"FAIL"),"PROBE_RESULT_REJECTED");
        return document.RootElement.Clone();
    }
    private static async Task ExecuteOwnerCasesAsync(CancellationToken ct)
    {
        var binaryHash = Digest(await File.ReadAllBytesAsync(Owner, ct));
        var input = new Dictionary<string, object?> {
            ["schema"]="zeka-m1-owner-provision/v1", ["scope"]="LocalSyntheticM1OwnerProvision", ["project"]="zeka-m1",
            ["authorityKind"]="OwnerAttestedLocalSynthetic", ["ownerDecisionId"]="a7daf3ea-6370-4604-9e4b-e5eb206465c7",
            ["approvalCommentId"]="6529021741", ["ownerAccount"]=@"DILI_LP\herve", ["expectedOwnerSid"]="S-1-5-21-1-2-3-1001",
            ["actualOwnerSid"]="S-1-5-21-1-2-3-1001", ["expectedOwnerProfile"]=@"C:\Users\herve", ["actualOwnerProfile"]=@"C:\Users\herve",
            ["normalizedEmail"]="HERVE.MBILO@GMAIL.COM", ["sourceCommit"]=SourceCommit, ["binarySha256"]=binaryHash,
            ["databaseSourceCommit"]=SourceCommit, ["databaseName"]="zeka_m1_auth", ["initializationId"]=InitializationId,
            ["subjectId"]=SubjectId, ["displayName"]="SQL corpus synthetic organisation", ["slug"]="sql-corpus-synthetic",
            ["idempotencyKey"]=IdempotencyKey, ["processingTimeoutSeconds"]=120, ["retentionSeconds"]=86400,
            ["authRuntimeConnectionString"]=RuntimeConnection("auth"), ["ownerPassword"]="Aa9!" + FreshPassword() };
        ActiveCase = "owner.synthetic-create";
        await InvokeAsync(Owner, [], JsonSerializer.Serialize(input), 0, ct, null, "OWNER_PROVISIONED");
        ActiveCase = "owner.synthetic-replay";
        await InvokeAsync(Owner, [], JsonSerializer.Serialize(input), 0, ct, null, "OWNER_PROVISIONING_REPLAY");
        input["displayName"] = "SQL corpus conflicting organisation";
        ActiveCase = "owner.synthetic-conflict";
        await InvokeAsync(Owner, [], JsonSerializer.Serialize(input), 4, ct, null, "OWNER_PROVISIONING_CONFLICT");
        input["databaseSourceCommit"] = new string('f', 40);
        ActiveCase = "owner.synthetic-marker-refusal";
        await InvokeAsync(Owner, [], JsonSerializer.Serialize(input), 2, ct, null, "RUNTIME_INITIALIZATION_REJECTED");
    }

    private static async Task InvokeMigrationAsync(string service, string operation, string target, int exit, CancellationToken ct)
    {
        var root = Path.Combine(EvidenceRoot, "migration-" + service + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, PrivateDirectory);
        await InvokeAsync(Migrator, [operation,"--service",service,"--target",target,"--operation-id",Guid.NewGuid().ToString("D"),
            "--evidence-root",root,"--evidence-file","result.json","--lock-timeout-seconds","10","--credential-stdin"],
            Connection("zeka_m1_"+service,"zeka_"+service+"_migrator",MigratorPasswords[service])+"\n",exit,ct);
    }

    private static async Task InvokeAsync(string tool, string[] arguments, string? standardInput, int expectedExit,
        CancellationToken ct, Dictionary<string,string>? environment = null, string? expectedCode = null, string? forbiddenOutput = null)
    {
        var start = new ProcessStartInfo(Dotnet) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(tool)! };
        start.ArgumentList.Add(tool);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null) foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        using var process = new Process { StartInfo = start };
        Require(process.Start(), "CLI_START_REJECTED");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            if (standardInput is not null) await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
        }
        catch { try { if (!process.HasExited) process.Kill(true); } catch { } throw; }
        var output = await stdout; var error = await stderr;
        if (forbiddenOutput is not null) Require(!output.Contains(forbiddenOutput,StringComparison.Ordinal) && !error.Contains(forbiddenOutput,StringComparison.Ordinal),"DIAGNOSTIC_DETAIL_LEAKED");
        string? code = null;
        string? stage = null;
        SafePostgresDiagnostic? diagnostic = null;
        // Never retain raw child output. Preserve typed safe provider fields and stable stage/code.
        try
        {
            using var parsed = JsonDocument.Parse(output);
            var value = parsed.RootElement;
            if (value.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String)
                code = SafeIdentifier(codeValue.GetString());
            if (value.TryGetProperty("stage", out var stageValue) && stageValue.ValueKind == JsonValueKind.String)
                stage = SafeIdentifier(stageValue.GetString());
            foreach (var field in new[] { "postgresDiagnostic", "postgres" })
                if (value.TryGetProperty(field, out var provider) && provider.ValueKind != JsonValueKind.Null)
                { diagnostic = SafePostgresDiagnostic.Parse(provider); break; }
        }
        catch (JsonException) { /* Non-JSON native output remains withheld, with byte counts. */ }
        catch (InvalidOperationException) { /* Malformed provider output fails its separate contract. */ }
        Results.Add(new { id = ActiveCase, nativeExit = process.ExitCode, stdoutBytes = Encoding.UTF8.GetByteCount(output),
            stderrBytes = Encoding.UTF8.GetByteCount(error), stage, code, postgres = diagnostic,
            status = process.ExitCode == expectedExit && (expectedCode is null || code == expectedCode) ? "PASS" : "FAIL" });
        if (process.ExitCode != expectedExit || expectedCode is not null && code != expectedCode)
        {
            if (tool == Setup && arguments.SequenceEqual(new[] { "--guard-checks" }) && File.Exists("/run/results/guard-checks.json"))
                File.Copy("/run/results/guard-checks.json", Path.Combine(EvidenceRoot, "guard-checks-failed.json"));
            throw new CorpusFailure("CLI_RESULT_UNEXPECTED");
        }
    }

    private static string? SafeIdentifier(string? value)
        => value is not null && value.Length is > 0 and <= 128 &&
           value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') ? value : null;
    private static async Task ExecuteAsync(NpgsqlConnection c, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    { await using var command = new NpgsqlCommand(sql,c,tx) { CommandTimeout=30 }; await command.ExecuteNonQueryAsync(ct); }
    private static string RuntimeConnection(string service) => Connection("zeka_m1_"+service,"zeka_"+service+"_runtime",RuntimePasswords[service]);
    private static string Connection(string database,string user,string password) => new NpgsqlConnectionStringBuilder {
        Host="postgres", Port=5432, Database=database, Username=user, Password=password, Pooling=false,
        Timeout=5, CommandTimeout=30, SslMode=SslMode.Disable, IncludeErrorDetail=false }.ConnectionString;
    private static string AdminConnection(string database) => new NpgsqlConnectionStringBuilder {
        Host=Socket, Port=5432, Database=database, Username="postgres", Pooling=false,
        Timeout=5, CommandTimeout=30, SslMode=SslMode.Disable, IncludeErrorDetail=false }.ConnectionString;
    private static string FreshPassword()
    {
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        FailureCapture.RegisterSecret(value);
        return value;
    }
    private static string Literal(string value) => "'"+value.Replace("'","''",StringComparison.Ordinal)+"'";
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static void Require(bool condition,string code) { if (!condition) throw new CorpusFailure(code); }
    private static async Task WritePrivateAsync(string path,string content,CancellationToken ct)
    {
        if (File.Exists(path)) Require((File.GetAttributes(path)&FileAttributes.ReparsePoint)==0 && File.GetUnixFileMode(path)==PrivateFile,"PRIVATE_FIXTURE_FILE_REJECTED");
        await using var file = new FileStream(path,new FileStreamOptions { Mode=FileMode.Create,Access=FileAccess.Write,Share=FileShare.None,UnixCreateMode=PrivateFile });
        var bytes=Encoding.UTF8.GetBytes(content); await file.WriteAsync(bytes,ct); await file.FlushAsync(ct);
    }
    private static async Task WriteReportAsync(string status,string? code,string? sqlState,CancellationToken ct)
    {
        var report=new { schema="zeka-owner-sql-corpus-result/v1",status,caseId=ActiveCase,code,sqlState,
            sourceMarker=SourceCommit,fixtureId=FixtureId,results=Results,postgres=FailureCapture.Diagnostic,windowsOwnerAttestation="NOT_RUN_SYNTHETIC_PROTOCOL_FIXTURE_ONLY",
            protectedM1="NOT_USED",sqlCoverage="REQUIRES_COMPLETE_SOURCE_BOUND_EXECUTION_SET_COMPARISON" };
        await WritePrivateAsync(Path.Combine(EvidenceRoot,"corpus-result.json"),JsonSerializer.Serialize(report,Json),ct);
    }
    private static Dictionary<string,string> Parse(string[] args)
    {
        Require(args.Length%2==0,"ARGUMENTS_REJECTED"); var result=new Dictionary<string,string>(StringComparer.Ordinal);
        for(var i=0;i<args.Length;i+=2) Require(args[i].StartsWith("--",StringComparison.Ordinal)&&result.TryAdd(args[i][2..],args[i+1]),"ARGUMENTS_REJECTED");
        var expected=new[]{"dotnet","setup","migrator","owner","source-root","case-file","evidence-root","fixture-host","fixture-port"};
        Require((result.Count==expected.Length || result.Count==expected.Length+1 && result.ContainsKey("probe"))
            && expected.All(result.ContainsKey) && result.Keys.All(key => key=="probe" || expected.Contains(key,StringComparer.Ordinal)),"ARGUMENTS_REJECTED"); return result;
    }
    private static string RequiredFile(Dictionary<string,string> options,string key)
    { var path=Path.GetFullPath(options[key]); Require(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)==0,"INPUT_FILE_REJECTED"); return path; }
    private sealed class CorpusFailure(string code):Exception("Synthetic SQL corpus rejected.") { public string Code {get;}=code; }
}
