using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zeka.LocalStack.DatabaseSetup;

/// <summary>Destructive role-only checks, admitted solely on a separately marked empty fixture cluster.</summary>
internal static class GuardChecks
{
    private const string Service = "auth";
    private const string Revision = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string[] Roles =
        ["zeka_auth_owner", "zeka_auth_migrator", "zeka_auth_runtime", "zeka_auth_closure_recovery"];

    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var cases = new List<GuardCase>();
        string? failure = null;
        var activeCase = "fixture_admission";
        var admitted = false;
        NpgsqlConnection? admin = null;
        string? fixture = null;
        try
        {
            fixture = Environment.GetEnvironmentVariable("ZEKA_M1_GUARD_FIXTURE");
            Require(fixture is not null && Regex.IsMatch(fixture,
                "^zeka-m1-bootstrap-fixture-[0-9a-f]{32}$", RegexOptions.CultureInvariant),
                "GUARD_FIXTURE_MARKER_REJECTED");
            Require((await File.ReadAllTextAsync("/run/secrets/guard-fixture-marker", ct))
                .TrimEnd('\r', '\n') == fixture, "GUARD_FIXTURE_MARKER_REJECTED");
            var adminPassword = (await File.ReadAllTextAsync("/run/secrets/admin-password", ct))
                .TrimEnd('\r', '\n');
            Require(adminPassword.Length is >= 24 and <= 512 && !adminPassword.Any(char.IsControl),
                "GUARD_FIXTURE_ADMIN_INPUT_REJECTED");
            var connection = Connection("postgres", adminPassword);
            admin = new NpgsqlConnection(connection);
            await admin.OpenAsync(ct);
            await AssertFixtureAsync(admin, fixture!, true, ct);
            admitted = true;
            cases.Add(new(activeCase, "passed"));
            var passwords = Passwords();

            activeCase = "new_roles_comments_and_password_roundtrip";
            Require(await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, true, ct) == 4,
                "GUARD_NEW_ROLE_COUNT_REJECTED");
            await AssertCommentsAsync(admin, fixture!, ct);
            foreach (var kind in new[] { "migrator", "runtime" })
            {
                await AssertLoginAsync(kind, passwords[kind], true, ct);
                await AssertLoginAsync(kind, FreshPassword(), false, ct);
            }
            cases.Add(new(activeCase, "passed"));

            activeCase = "repeat_preserves_oids_comments_and_password_hashes";
            var before = await SnapshotAsync(admin, ct);
            Require(await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, false, ct) == 0,
                "GUARD_REPEAT_CREATED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            cases.Add(new(activeCase, "passed"));
            await CleanupAsync(admin, ct);

            activeCase = "unmarked_existing_role_rejected_without_partial_creation";
            await ExecuteAsync(admin, "CREATE ROLE zeka_auth_runtime LOGIN NOSUPERUSER NOBYPASSRLS " +
                "NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION CONNECTION LIMIT -1", ct);
            before = await SnapshotAsync(admin, ct);
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                passwords, true, ct), "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            Require(before.Count == 1, "GUARD_UNMARKED_ROLE_COUNT_REJECTED");
            cases.Add(new(activeCase, "passed"));
            await CleanupAsync(admin, ct);

            activeCase = "missing_comment_rejected_untouched";
            await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, true, ct);
            await ExecuteAsync(admin, "COMMENT ON ROLE zeka_auth_owner IS NULL", ct);
            before = await SnapshotAsync(admin, ct);
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                passwords, true, ct), "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            cases.Add(new(activeCase, "passed"));
            await CleanupAsync(admin, ct);

            activeCase = "foreign_initialization_comment_rejected_untouched";
            await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, true, ct);
            await ExecuteAsync(admin, "COMMENT ON ROLE zeka_auth_runtime IS 'foreign-initialization'", ct);
            before = await SnapshotAsync(admin, ct);
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                passwords, true, ct), "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            cases.Add(new(activeCase, "passed"));
            await CleanupAsync(admin, ct);

            activeCase = "attribute_drift_rejected_untouched";
            await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, true, ct);
            await ExecuteAsync(admin, "ALTER ROLE zeka_auth_runtime CREATEDB", ct);
            before = await SnapshotAsync(admin, ct);
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                passwords, true, ct), "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            cases.Add(new(activeCase, "passed"));
            await CleanupAsync(admin, ct);

            activeCase = "missing_saved_password_rejected_without_partial_creation";
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                new Dictionary<string, string> { ["runtime"] = passwords["runtime"] }, true, ct),
                "ROLE_REGISTRY_INPUT_REJECTED");
            Require((await SnapshotAsync(admin, ct)).Count == 0, "GUARD_PARTIAL_ROLE_CREATION");
            cases.Add(new(activeCase, "passed"));

            activeCase = "missing_established_role_rejected_without_partial_creation";
            await RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service, passwords, true, ct);
            await ExecuteAsync(admin, "DROP ROLE zeka_auth_runtime", ct);
            before = await SnapshotAsync(admin, ct);
            await AssertRejectedAsync(() => RoleRegistry.EnsureAsync(admin, fixture!, Revision, Service,
                passwords, false, ct), "ROLE_REGISTRY_MISSING_ESTABLISHED_ROLE");
            await AssertSnapshotAsync(admin, before, ct);
            cases.Add(new(activeCase, "passed"));
        }
        catch (GuardFailure error) { failure = error.Message; }
        catch (OperationCanceledException) { failure = "GUARD_DEADLINE_OR_CANCELLATION"; }
        catch { failure = "GUARD_OPERATION_REJECTED"; }
        finally
        {
            if (failure is null && admitted && admin is not null)
            {
                using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await AssertFixtureAsync(admin, fixture!, false, cleanupDeadline.Token);
                    await CleanupAsync(admin, cleanupDeadline.Token);
                    Require((await SnapshotAsync(admin, cleanupDeadline.Token)).Count == 0,
                        "GUARD_CLEANUP_REJECTED");
                }
                catch { failure ??= "GUARD_CLEANUP_REJECTED"; }
            }
            if (admin is not null)
            {
                try { await admin.DisposeAsync(); }
                catch { failure ??= "GUARD_CONNECTION_DISPOSAL_REJECTED"; }
            }
        }
        if (failure is not null) cases.Add(new(activeCase, "failed"));
        var report = new
        {
            schema = "zeka-m1-bootstrap-guards/v1",
            status = failure is null ? "passed" : "failed",
            passed = cases.Count(value => value.Status == "passed"),
            failureCode = failure,
            cases,
            scope = "Dedicated marked PostgreSQL fixture; fixed auth roles only; no application databases.",
            limit = "Successful committed role/comment creation and rejection atomicity were checked; " +
                "no injected mid-CREATE rollback was exercised."
        };
        try
        {
            await using var output = new FileStream("/run/results/guard-checks.json", FileMode.CreateNew,
                FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true });
            await output.FlushAsync();
        }
        catch { return 1; }
        return failure is null ? 0 : 1;
    }

    private static async Task AssertFixtureAsync(NpgsqlConnection admin, string marker, bool requireFresh,
        CancellationToken ct)
    {
        const string sql = """
            SELECT current_database()='postgres' AND session_user='postgres' AND current_user='postgres'
                AND inet_server_addr() IS NOT NULL
                AND (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname=current_user)
                AND (SELECT pg_catalog.shobj_description(oid,'pg_database') FROM pg_catalog.pg_database
                     WHERE datname='postgres')=@marker
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database
                                WHERE datname NOT IN ('postgres','template0','template1'))
                AND current_setting('log_statement')='none'
                AND current_setting('log_min_error_statement')='panic'
                AND (NOT @fresh OR NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                     WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_'))
            """;
        await using var command = Command(admin, sql);
        command.Parameters.AddWithValue("marker", marker);
        command.Parameters.AddWithValue("fresh", requireFresh);
        Require(await command.ExecuteScalarAsync(ct) is true, "GUARD_FIXTURE_IDENTITY_REJECTED");
    }

    private static async Task AssertCommentsAsync(NpgsqlConnection admin, string initializationId,
        CancellationToken ct)
    {
        await using var command = Command(admin,
            "SELECT rolname,pg_catalog.shobj_description(oid,'pg_authid') FROM pg_catalog.pg_roles " +
            "WHERE rolname=ANY(@roles)");
        command.Parameters.AddWithValue("roles", Roles);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var count = 0;
        while (await reader.ReadAsync(ct))
        {
            var kind = reader.GetString(0)["zeka_auth_".Length..];
            var expected = JsonSerializer.Serialize(new
            { schema = "zeka-m1-role/v1", initializationId, service = Service, kind, sourceCommit = Revision });
            Require(!reader.IsDBNull(1) && reader.GetString(1) == expected, "GUARD_COMMENT_REJECTED");
            count++;
        }
        Require(count == 4, "GUARD_COMMENT_COUNT_REJECTED");
    }

    private static async Task<List<string>> SnapshotAsync(NpgsqlConnection admin, CancellationToken ct)
    {
        // Password hashes remain in process memory and are never included in results or exceptions.
        const string sql = """
            SELECT row_to_json(s)::text FROM (
                SELECT oid,rolname,rolsuper,rolinherit,rolcreaterole,rolcreatedb,rolcanlogin,
                       rolreplication,rolbypassrls,rolconnlimit,rolconfig,rolvaliduntil,
                       md5(coalesce(rolpassword,'')) AS password_fingerprint,
                       pg_catalog.shobj_description(oid,'pg_authid') AS comment
                FROM pg_catalog.pg_authid WHERE rolname=ANY(@roles) ORDER BY rolname
            ) s
            """;
        await using var command = Command(admin, sql);
        command.Parameters.AddWithValue("roles", Roles);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var snapshot = new List<string>();
        while (await reader.ReadAsync(ct)) snapshot.Add(reader.GetString(0));
        return snapshot;
    }

    private static async Task AssertSnapshotAsync(NpgsqlConnection admin, List<string> before,
        CancellationToken ct) => Require(before.SequenceEqual(await SnapshotAsync(admin, ct)),
        "GUARD_REJECTION_OR_REPEAT_MUTATED_ROLE");

    private static async Task AssertRejectedAsync(Func<Task<int>> operation, string expected)
    {
        try { await operation(); }
        catch (InvalidOperationException error) when (error.Message == expected) { return; }
        throw new GuardFailure("GUARD_EXPECTED_REJECTION_ABSENT");
    }

    private static async Task AssertLoginAsync(string kind, string password, bool shouldPass,
        CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(Connection("zeka_auth_" + kind, password));
            await connection.OpenAsync(ct);
            await using var command = Command(connection, "SELECT session_user=current_user AND current_user=@role");
            command.Parameters.AddWithValue("role", "zeka_auth_" + kind);
            Require(await command.ExecuteScalarAsync(ct) is true && shouldPass, "GUARD_PASSWORD_AUTH_REJECTED");
        }
        catch (PostgresException error) when (error.SqlState == "28P01" && !shouldPass) { }
    }

    private static async Task CleanupAsync(NpgsqlConnection admin, CancellationToken ct)
    {
        foreach (var role in Roles)
            await ExecuteAsync(admin, "DROP ROLE IF EXISTS " + new NpgsqlCommandBuilder().QuoteIdentifier(role), ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection admin, string sql, CancellationToken ct)
    {
        await using var command = Command(admin, sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static string Connection(string user, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = "postgres-guard", Port = 5432, Database = "postgres", Username = user, Password = password,
        Pooling = false, Timeout = 10, CommandTimeout = 15, IncludeErrorDetail = false,
        LogParameters = false, Enlist = false
    }.ConnectionString;
    private static Dictionary<string, string> Passwords() => new()
        { ["migrator"] = FreshPassword(), ["runtime"] = FreshPassword() };
    private static string FreshPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql) =>
        new(sql, connection) { CommandTimeout = 15 };
    private static void Require(bool condition, string code)
    { if (!condition) throw new GuardFailure(code); }
    private sealed class GuardFailure(string code) : InvalidOperationException(code);
    private sealed record GuardCase(string Name, string Status);
}
