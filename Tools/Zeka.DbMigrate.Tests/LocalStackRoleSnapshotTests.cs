using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Zeka.LocalStack.DatabaseSetup;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class LocalStackRoleSnapshotTests
{
    // Synthetic input metadata; execution provenance is supplied by the exact-head runner.
    private const string Revision = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string[] Roles =
        ["zeka_auth_owner", "zeka_auth_migrator", "zeka_auth_runtime", "zeka_auth_closure_recovery"];
    private readonly ITestOutputHelper output;

    public LocalStackRoleSnapshotTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    public async Task Actual_snapshot_preserves_catalogue_identity_and_rejects_every_managed_role_setting_scope()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = deadline.Token;
        var stage = "fixture_admission";
        try
        {
            var fixture = await GetFixtureAsync(ct);
            await using var admin = new NpgsqlConnection(Connection(fixture, await ReadPasswordAsync(fixture.AdminPasswordFile, ct)));
            await admin.OpenAsync(ct);
            await AssertFixtureAsync(admin, fixture, ct);
            var passwords = new Dictionary<string, string>
            {
                ["migrator"] = FreshPassword(),
                ["runtime"] = FreshPassword()
            };

            stage = "create_attributable_roles";
            Assert.True(await RoleRegistry.EnsureAsync(admin, fixture.Marker, Revision, "auth", passwords, true, ct) == 4,
                "The regression requires four genuinely created roles.");

            stage = "actual_nonempty_snapshot";
            var before = await GuardChecks.SnapshotAsync(admin, ct);
            Assert.True(before.Count == Roles.Length, "The actual snapshot must contain all four roles.");
            var oids = new HashSet<long>();
            foreach (var row in before)
            {
                using var json = JsonDocument.Parse(row.RoleState);
                var state = json.RootElement;
                var name = state.GetProperty("rolname").GetString()!;
                Assert.True(Roles.Contains(name, StringComparer.Ordinal), "Unexpected role in the actual snapshot.");
                var oid = state.GetProperty("oid").GetInt64();
                Assert.True(oid > 0 && oids.Add(oid), "Snapshot role OIDs must be real and distinct.");
                Assert.True(!state.GetProperty("rolsuper").GetBoolean()
                    && !state.GetProperty("rolinherit").GetBoolean()
                    && !state.GetProperty("rolcreaterole").GetBoolean()
                    && !state.GetProperty("rolcreatedb").GetBoolean()
                    && !state.GetProperty("rolreplication").GetBoolean()
                    && !state.GetProperty("rolbypassrls").GetBoolean()
                    && state.GetProperty("rolconnlimit").GetInt32() == -1
                    && state.GetProperty("rolconfig").ValueKind == JsonValueKind.Null
                    && state.GetProperty("rolvaliduntil").ValueKind == JsonValueKind.Null,
                    "The snapshot must retain the full restricted role attributes.");
                var login = name is "zeka_auth_migrator" or "zeka_auth_runtime";
                Assert.True(state.GetProperty("rolcanlogin").GetBoolean() == login
                    && row.HasPasswordFingerprint == login, "Login and real password-verifier presence must be preserved.");
                var kind = name["zeka_auth_".Length..];
                var expectedComment = JsonSerializer.Serialize(new
                {
                    schema = "zeka-m1-role/v1", initializationId = fixture.Marker,
                    service = "auth", kind, sourceCommit = Revision
                });
                Assert.True(state.GetProperty("comment").GetString() == expectedComment,
                    "Actual PostgreSQL comments must retain the initialization/source identity.");

                // Independent, narrowly selected provider facts make an empty/masked
                // or self-compared snapshot insufficient. The verifier stays in memory.
                await using var command = new NpgsqlCommand(
                    "SELECT oid::bigint,md5(coalesce(rolpassword,'')) FROM pg_catalog.pg_authid WHERE rolname=@role", admin);
                command.Parameters.AddWithValue("role", name);
                await using var reader = await command.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), "Expected provider role was absent.");
                Assert.True(reader.GetInt64(0) == oid && row.MatchesFingerprint(reader.GetString(1)),
                    "Snapshot OID and private fingerprint must match actual provider facts.");
            }

            stage = "repeat_ensure_preserves_actual_snapshot";
            Assert.True(await RoleRegistry.EnsureAsync(admin, fixture.Marker, Revision, "auth", passwords, false, ct) == 0,
                "Repeat ensure must not create or adopt a role.");
            Assert.True(before.SequenceEqual(await GuardChecks.SnapshotAsync(admin, ct)),
                "Repeat ensure changed an OID, attribute, comment, setting or private password fingerprint.");

            foreach (var role in Roles)
            {
                foreach (var database in new string?[] { null, "postgres", "template1" })
                {
                    var scope = database is null ? "global" : database;
                    stage = "settings_drift_" + role + "_" + scope;
                    var inDatabase = database is null ? "" : " IN DATABASE " + Quote(database);
                    await ExecuteAsync(admin, "ALTER ROLE " + Quote(role) + inDatabase + " SET statement_timeout='1s'", ct);
                    await AssertSettingsRejectedAsync(() => GuardChecks.SnapshotAsync(admin, ct));
                    await AssertRegistrySettingsRejectedAsync(() =>
                        RoleRegistry.EnsureAsync(admin, fixture.Marker, Revision, "auth", passwords, false, ct));
                    // This is deliberate fixture injection/restoration after an expected
                    // rejection, not repair of a failed or retained installation.
                    await ExecuteAsync(admin, "ALTER ROLE " + Quote(role) + inDatabase + " RESET ALL", ct);
                    Assert.True(before.SequenceEqual(await GuardChecks.SnapshotAsync(admin, ct)),
                        "Restored fixture settings must recover the same role identity and verifier.");
                }
            }

            stage = "completed";
            output.WriteLine("Actual PG17 snapshot: four roles; repeat unchanged; all four managed roles rejected in global/current/other-database setting scopes.");
        }
        catch (Exception error)
        {
            // Never attach the original exception: provider SQL elsewhere contains
            // saved passwords. Preserve only attribution fields that cannot reveal it.
            var sqlState = (error as PostgresException)?.SqlState ?? "none";
            throw new InvalidOperationException(
                "PG17_SNAPSHOT_REGRESSION_REJECTED stage=" + stage
                + "; type=" + error.GetType().Name + "; sqlState=" + sqlState);
        }
        // Fixture roles/resources remain for diagnosis and exact-head evidence. The
        // disposable CI runner owns eventual runner teardown; no retained M1 cleanup.
    }

    private async Task<Fixture> GetFixtureAsync(CancellationToken ct)
    {
        var supplied = Environment.GetEnvironmentVariable("ZEKA_M1_PG17_REGRESSION_CONFIG");
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (!Path.IsPathFullyQualified(supplied) || new FileInfo(supplied).LinkTarget is not null)
                throw new InvalidOperationException("EXTERNAL_FIXTURE_CONFIG_REJECTED");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(supplied, ct));
            var names = document.RootElement.EnumerateObject().Select(value => value.Name).ToArray();
            var expected = new[] { "Host", "Port", "Database", "AdminPasswordFile", "FixtureId", "EngineId" };
            if (names.Length != expected.Length || names.Distinct(StringComparer.Ordinal).Count() != expected.Length
                || names.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
                throw new InvalidOperationException("EXTERNAL_FIXTURE_CONFIG_REJECTED");
            var fixture = JsonSerializer.Deserialize<Fixture>(document.RootElement.GetRawText())
                ?? throw new InvalidOperationException("EXTERNAL_FIXTURE_CONFIG_REJECTED");
            if (fixture.EngineId != "0b62f5ec-15d9-4e4b-a450-817d52378eef"
                || fixture.Host != "postgres-guard" || fixture.Port != 5432 || fixture.Database != "postgres"
                || fixture.AdminPasswordFile != "/run/secrets/admin-password"
                || !Regex.IsMatch(fixture.FixtureId, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("EXTERNAL_FIXTURE_CONFIG_REJECTED");
            output.WriteLine("Using explicitly admitted fixture " + fixture.FixtureId + "; no Docker creation from provider tests.");
            return fixture;
        }

        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("EXPLICIT_FIXTURE_CONFIG_REQUIRED_OUTSIDE_LINUX_CI");

        var id = Guid.NewGuid().ToString("N");
        var name = "zeka-m1-pg17-provider-" + id;
        var labels = new Dictionary<string, string>
        {
            ["com.diliwo.zeka.fixture-id"] = id,
            ["com.diliwo.zeka.owner"] = "codex",
            ["com.diliwo.zeka.scope"] = "m1-pg17-regression",
            ["com.docker.compose.project"] = name
        };
        var directory = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var passwordFile = Path.Combine(directory, "admin-password");
        // The host parent has owner-only traversal before writing. The new file is
        // readable through its read-only bind by postgres UID999; no existing ACL or
        // source file is broadened. The secret is absent from container environment.
        await using (var file = new FileStream(passwordFile, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead
        }))
        {
            var bytes = Encoding.UTF8.GetBytes(FreshPassword());
            try { await file.WriteAsync(bytes, ct); file.Flush(true); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var network = new NetworkBuilder().WithName(name + "-internal").WithLabel(labels)
            .WithCleanUp(false).WithCreateParameterModifier(value => value.Internal = true).Build();
        await network.CreateAsync(ct);
        var data = new VolumeBuilder().WithName(name + "-data").WithLabel(labels)
            .WithCleanUp(false).Build();
        await data.CreateAsync(ct);
        var container = new ContainerBuilder().WithImage("postgres:17-alpine").WithName(name + "-postgres")
            .WithLabel(labels).WithCleanUp(false).WithAutoRemove(false).WithNetwork(network)
            .WithEnvironment("POSTGRES_USER", "postgres")
            .WithEnvironment("POSTGRES_PASSWORD_FILE", "/run/secrets/admin-password")
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256")
            .WithBindMount(passwordFile, "/run/secrets/admin-password", AccessMode.ReadOnly)
            .WithVolumeMount(data, "/var/lib/postgresql/data", AccessMode.ReadWrite)
            .WithCommand("-c", "log_statement=none", "-c", "log_min_error_statement=panic")
            .WithLogger(NullLogger.Instance)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres"]))
            .Build();
        await container.StartAsync(ct);
        output.WriteLine("Owned Linux CI fixture " + id + "; networkName=" + network.Name + "; container=" + container.Id + "; volume=" + data.Name + "; no published database ports.");
        var result = new Fixture(container.IpAddress, 5432, "postgres", passwordFile, id, "linux-ci-owned-fixture");
        await using var admin = new NpgsqlConnection(Connection(result, await ReadPasswordAsync(passwordFile, ct)));
        await admin.OpenAsync(ct);
        await ExecuteAsync(admin, "COMMENT ON DATABASE postgres IS " + Literal(result.Marker), ct);
        return result;
    }

    private static async Task AssertFixtureAsync(NpgsqlConnection admin, Fixture fixture, CancellationToken ct)
    {
        const string sql = """
            SELECT current_database()='postgres' AND session_user='postgres' AND current_user='postgres'
                AND inet_server_addr() IS NOT NULL
                AND current_setting('server_version_num')::int / 10000 = 17
                AND (SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname=current_user)
                AND (SELECT pg_catalog.shobj_description(oid,'pg_database')
                     FROM pg_catalog.pg_database WHERE datname='postgres')=@marker
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database
                                WHERE datname NOT IN ('postgres','template0','template1'))
                AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_')
                AND current_setting('log_statement')='none'
                AND current_setting('log_min_error_statement')='panic'
            """;
        await using var command = new NpgsqlCommand(sql, admin) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("marker", fixture.Marker);
        Assert.True(await command.ExecuteScalarAsync(ct) is true,
            "Provider regression requires its admitted fresh synthetic PostgreSQL 17 fixture.");
    }

    private static async Task AssertSettingsRejectedAsync(Func<Task<List<GuardChecks.RoleSnapshot>>> action)
    {
        var rejected = false;
        try { _ = await action(); }
        catch (InvalidOperationException error) when (error.Message == "GUARD_ROLE_SETTINGS_REJECTED") { rejected = true; }
        Assert.True(rejected, "The actual snapshot accepted managed-role configuration drift.");
    }

    private static async Task AssertRegistrySettingsRejectedAsync(Func<Task<int>> action)
    {
        var rejected = false;
        try { _ = await action(); }
        catch (InvalidOperationException error) when (error.Message == "ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE") { rejected = true; }
        Assert.True(rejected, "The role registry accepted managed-role configuration drift.");
    }

    private static async Task<string> ReadPasswordAsync(string path, CancellationToken ct)
    {
        var password = (await File.ReadAllTextAsync(path, ct)).TrimEnd('\r', '\n');
        if (!Regex.IsMatch(password, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("FIXTURE_PASSWORD_INPUT_REJECTED");
        return password;
    }

    private static string FreshPassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Connection(Fixture fixture, string password) => new NpgsqlConnectionStringBuilder
    {
        Host = fixture.Host, Port = fixture.Port, Database = fixture.Database, Username = "postgres",
        Password = password, Pooling = false, Timeout = 5, CommandTimeout = 30, IncludeErrorDetail = false
    }.ConnectionString;
    private static string Quote(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static async Task ExecuteAsync(NpgsqlConnection admin, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, admin) { CommandTimeout = 30 };
        await command.ExecuteNonQueryAsync(ct);
    }
    private sealed record Fixture(string Host, int Port, string Database, string AdminPasswordFile, string FixtureId, string EngineId)
    {
        internal string Marker => "zeka-m1-bootstrap-fixture-" + FixtureId;
    }
}
