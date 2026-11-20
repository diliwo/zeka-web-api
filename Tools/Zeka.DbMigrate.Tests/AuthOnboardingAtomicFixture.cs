extern alias DbMigrator;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthManager.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using SafePostgresDiagnosticCapture = DbMigrator::Zeka.ToolingDiagnostics.SafePostgresDiagnosticCapture;

namespace Zeka.DbMigrate.Tests;

// A single wholly owned cluster, with a fresh Auth database per serial scenario.
// No retained fixture/configuration is accepted. Secret bytes enter only a 0400
// copied container file; it is not described as a read-only bind mount.
public sealed class AuthOnboardingAtomicFixture : IAsyncLifetime
{
    private const string SecretPath = "/run/secrets/admin-password";
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string adminSecret = FreshSecret();
    private readonly string migratorSecret = FreshSecret();
    private readonly string runtimeSecret = FreshSecret();
    private INetwork? network;
    private IVolume? volume;
    private IContainer? container;
    private bool failed;
    private ushort port;
    private string project = string.Empty;
    private string stage = "fixture-construction";

    public async Task InitializeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        try
        {
            project = "zeka-auth-atomic-tests-" + id;
            var labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = project,
                ["com.diliwo.zeka.fixture-id"] = id,
                ["com.diliwo.zeka.scope"] = "auth-onboarding-atomic-provider-tests",
                ["com.diliwo.zeka.owner"] = "codex"
            };
            stage = "owned-network-volume";
            // Docker29 skips host port mapping on internal-only networks. Native Windows
            // tests use a fresh dedicated bridge, strict loopback-only publication, and
            // actual membership/port readback below; no unrelated network is reused.
            network = new NetworkBuilder().WithName(project + "-private").WithLabel(labels)
                .WithCleanUp(false).WithCreateParameterModifier(value => value.Internal = false).Build();
            await network.CreateAsync(ct);
            volume = new VolumeBuilder().WithName(project + "-data").WithLabel(labels).WithCleanUp(false).Build();
            await volume.CreateAsync(ct);
            var bytes = Encoding.UTF8.GetBytes(adminSecret);
            try
            {
                // Generic builder preserves PostgreSQL durability defaults. The PG module
                // adds password environment values and disables fsync/synchronous_commit.
                container = new ContainerBuilder().WithImage("postgres:17-alpine").WithName(project + "-postgres")
                    .WithLabel(labels).WithNetwork(network).WithCleanUp(false).WithAutoRemove(false)
                    .WithEnvironment("POSTGRES_USER", "postgres")
                    .WithEnvironment("POSTGRES_PASSWORD_FILE", SecretPath)
                    .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256")
                    .WithResourceMapping(bytes, SecretPath, uid: 70, gid: 70, fileMode: UnixFileModes.UserRead)
                    .WithVolumeMount(volume, "/var/lib/postgresql/data", AccessMode.ReadWrite)
                    .WithPortBinding(5432, true)
                    .WithCreateParameterModifier(parameters =>
                    {
                        if (parameters.HostConfig.PortBindings.Count != 1)
                            throw new InvalidOperationException("FIXTURE_PORT_BOUNDARY_REJECTED");
                        foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                        foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
                    })
                    .WithCommand("-c", "log_statement=none", "-c", "log_min_error_statement=panic")
                    .WithLogger(NullLogger.Instance)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                        ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres"]))
                    .Build();
                stage = "owned-container-start";
                await container.StartAsync(ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            port = container.GetMappedPublicPort(5432);
            stage = "actual-docker-resource-admission";
            await InspectAsync(ct);
            var mode = await container.ExecAsync(["stat", "-c", "%u:%g:%a", SecretPath], ct);
            Assert.Equal(0, mode.ExitCode);
            Assert.Equal("70:70:400", mode.Stdout.Trim());
            stage = "fresh-postgresql-and-durability-admission";
            await using var connection = await OpenAdminAsync("postgres", ct);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('server_version_num')::integer/10000=17
                  AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on'
                  AND current_setting('synchronous_commit')='on'
                  AND session_user='postgres' AND current_user='postgres' AND current_database()='postgres'
                  AND current_setting('log_statement')='none' AND current_setting('log_min_error_statement')='panic'
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_database WHERE datname NOT IN ('postgres','template0','template1'))
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_')
                """, connection);
            Assert.True(await command.ExecuteScalarAsync(ct) is true, "Fresh PG17/default durability admission failed.");
        }
        catch (Exception error) { failed = true; throw SafeFailure(error); }
    }

    public async Task DisposeAsync()
    {
        // CleanUp=false deliberately retains every owned container/data/network for
        // inspection. Only this exact new container is stopped; nothing is deleted.
        using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (container is not null) await container.StopAsync(stopBudget.Token);
    }

    internal async Task RunAsync(string name, ITestOutputHelper output, Func<AuthOnboardingAtomicDatabase, CancellationToken, Task> run)
    {
        if (failed) throw new InvalidOperationException("PREVIOUS_ATOMIC_PROVIDER_CASE_FAILED_NO_FURTHER_DATABASE_WORK");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            output.WriteLine(JsonSerializer.Serialize(new { scenario = name, fixtureId = id, project,
                containerId = container!.Id, network = network!.Name, volume = volume!.Name,
                image = "postgres:17-alpine", hostBinding = "127.0.0.1", port, retained = true,
                dedicatedBridge = true, unrelatedContainersAttached = false, outboundInternal = false }));
            stage = "fresh-auth-database-baseline:" + name;
            var databaseName = "auth_atomic_" + Guid.NewGuid().ToString("N");
            await using (var admin = await OpenAdminAsync("postgres", deadline.Token))
            await using (var create = new NpgsqlCommand("CREATE DATABASE " + Quote(databaseName), admin))
                await create.ExecuteNonQueryAsync(deadline.Token);
            var database = new AuthOnboardingAtomicDatabase(this, databaseName);
            await database.PrepareBaselineAsync(deadline.Token);
            stage = "provider-scenario:" + name;
            await run(database, deadline.Token);
            output.WriteLine(JsonSerializer.Serialize(new { scenario = name, outcome = "PASS", database = databaseName,
                fullVerifier = "production-auth-v20", actualPostgreSqlMajor = 17, noRetainedResourceAccess = true }));
        }
        catch (Exception error) { failed = true; throw SafeFailure(error); }
    }

    internal string MigratorConnection(string database) => Connection(database, "zeka_auth_migrator", migratorSecret);
    internal string RuntimeConnection(string database) => Connection(database, "zeka_auth_runtime", runtimeSecret);
    internal async Task<NpgsqlConnection> OpenAdminAsync(string database, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(Connection(database, "postgres", adminSecret));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    internal string RolePasswordSql() => "ALTER ROLE zeka_auth_migrator PASSWORD " + Literal(migratorSecret)
        + "; ALTER ROLE zeka_auth_runtime PASSWORD " + Literal(runtimeSecret) + ";";

    private string Connection(string database, string role, string secret) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = port, Database = database, Username = role, Password = secret,
        Pooling = false, Timeout = 5, CommandTimeout = 30, IncludeErrorDetail = false,
        ApplicationName = "zeka-auth-atomic-provider-" + id
    }.ConnectionString;

    private async Task InspectAsync(CancellationToken ct)
    {
        const string format = "{\"id\":{{json .Id}},\"labels\":{{json .Config.Labels}},\"requestedPorts\":{{json .HostConfig.PortBindings}},\"ports\":{{json .NetworkSettings.Ports}},\"mounts\":{{json .Mounts}},\"networks\":{{json .NetworkSettings.Networks}},\"env\":{{json .Config.Env}},\"privileged\":{{json .HostConfig.Privileged}}}";
        var root = await DockerJsonAsync(["inspect", "--format", format, container!.Id], ct);
        Assert.Equal(container.Id, root.GetProperty("id").GetString());
        Assert.Equal(project, root.GetProperty("labels").GetProperty("com.docker.compose.project").GetString());
        Assert.Equal(id, root.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.False(root.GetProperty("privileged").GetBoolean());
        var requested = root.GetProperty("requestedPorts").EnumerateObject().ToArray();
        Assert.Single(requested);
        Assert.Equal("5432/tcp", requested[0].Name);
        var requestedBinding = Assert.Single(requested[0].Value.EnumerateArray());
        Assert.Equal("127.0.0.1", requestedBinding.GetProperty("HostIp").GetString());
        // The empty create-request HostPort requests random allocation; validate
        // its actual nonempty assigned value through NetworkSettings.Ports.
        var ports = root.GetProperty("ports").EnumerateObject().ToArray();
        Assert.Single(ports);
        Assert.Equal("5432/tcp", ports[0].Name);
        var binding = Assert.Single(ports[0].Value.EnumerateArray());
        Assert.Equal("127.0.0.1", binding.GetProperty("HostIp").GetString());
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.GetProperty("HostPort").GetString());
        var attached = root.GetProperty("networks").EnumerateObject().ToArray();
        Assert.Single(attached);
        Assert.Equal(network!.Name, attached[0].Name);
        var mount = Assert.Single(root.GetProperty("mounts").EnumerateArray());
        Assert.Equal("volume", mount.GetProperty("Type").GetString());
        Assert.Equal(volume!.Name, mount.GetProperty("Name").GetString());
        Assert.Equal("/var/lib/postgresql/data", mount.GetProperty("Destination").GetString());
        Assert.True(mount.GetProperty("RW").GetBoolean());
        var environment = root.GetProperty("env").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.True(environment.Contains("POSTGRES_PASSWORD_FILE=" + SecretPath, StringComparer.Ordinal));
        Assert.False(environment.Any(value => value.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal)));

        const string networkFormat = "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"internal\":{{json .Internal}},\"driver\":{{json .Driver}},\"labels\":{{json .Labels}},\"containers\":{{json .Containers}}}";
        var actualNetwork = await DockerJsonAsync(["network", "inspect", "--format", networkFormat, network.Name], ct);
        Assert.Equal(attached[0].Value.GetProperty("NetworkID").GetString(), actualNetwork.GetProperty("id").GetString());
        Assert.Equal(network.Name, actualNetwork.GetProperty("name").GetString());
        Assert.False(actualNetwork.GetProperty("internal").GetBoolean());
        Assert.Equal("bridge", actualNetwork.GetProperty("driver").GetString());
        Assert.Equal(id, actualNetwork.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.Equal(container.Id, Assert.Single(actualNetwork.GetProperty("containers").EnumerateObject()).Name);
        const string volumeFormat = "{\"name\":{{json .Name}},\"driver\":{{json .Driver}},\"labels\":{{json .Labels}}}";
        var actualVolume = await DockerJsonAsync(["volume", "inspect", "--format", volumeFormat, volume.Name], ct);
        Assert.Equal(volume.Name, actualVolume.GetProperty("name").GetString());
        Assert.Equal("local", actualVolume.GetProperty("driver").GetString());
        Assert.Equal(id, actualVolume.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        // Raw Docker response/environment is never logged or included in receipts.
    }

    private async Task<JsonElement> DockerJsonAsync(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_INSPECT_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch { /* Preserve the first admission/deadline failure. */ }
            throw;
        }
        var raw = await stdout;
        var nativeError = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("FIXTURE_INSPECT_FAILED:" + process.ExitCode + ":" + SafeText(nativeError));
        using var parsed = JsonDocument.Parse(raw);
        return parsed.RootElement.Clone();
    }
    private InvalidOperationException SafeFailure(Exception error)
    {
        var capture = new SafePostgresDiagnosticCapture();
        foreach (var secret in new[] { adminSecret, migratorSecret, runtimeSecret }) capture.RegisterSecret(secret);
        capture.Capture(error);
        var detail = capture.Diagnostic is null ? SafeText(error.Message) : JsonSerializer.Serialize(capture.Diagnostic);
        return new InvalidOperationException("AUTH_ATOMIC_PROVIDER_FAILURE stage=" + stage + " category=" + error.GetType().Name + " detail=" + detail);
    }
    private string SafeText(string value)
    {
        foreach (var secret in new[] { adminSecret, migratorSecret, runtimeSecret }) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"(?i)(?:password|pwd)\s*=\s*[^;\r\n]*", "[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return value.Length > 2048 ? value[..2048] + "[truncated]" : value;
    }
    private static string FreshSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed class AuthOnboardingAtomicDatabase(AuthOnboardingAtomicFixture fixture, string name)
{
    private const string Previous = "20261018144506_BrowserSessionSnapshot";
    private const string Target = "20261115195218_OnboardingRegistration";
    private static readonly string[] Enrollment = ["EmailVerificationChallenges", "OnboardingGrants", "OnboardingOperationReceipts", "VerificationMailOutboxMessages"];
    private readonly string repository = FindRepository();

    internal async Task PrepareBaselineAsync(CancellationToken ct)
    {
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(repository, "Deployments/database/bootstrap-auth-roles.sql"), ct);
        await using (var admin = await fixture.OpenAdminAsync(name, ct))
        {
            await ExecuteAsync(admin, bootstrap, ct);
            await ExecuteAsync(admin, fixture.RolePasswordSql(), ct);
        }
        await using (var owner = await OpenOwnerAsync(ct))
        await using (var database = Context(owner))
            await database.GetService<IMigrator>().MigrateAsync(Previous, ct);
        await using (var admin = await fixture.OpenAdminAsync(name, ct)) await ExecuteAsync(admin, bootstrap, ct);
        await AssertTargetAbsentAsync(ct);
    }

    internal Task<EngineResult> ExecuteAsync(CancellationToken ct, AuthOnboardingHooks? hooks = null)
    {
        Assert.True(ServiceDescriptor.TryGet("auth", out var service));
        var options = new CommandOptions(MigrationOperation.Apply, service, Target, Guid.NewGuid(),
            Path.GetTempPath(), "provider-source-only.json", TimeSpan.FromSeconds(5));
        // This is the actual Git base, not a clean-commit claim. The dirty source
        // snapshot and built test/executor DLL hashes are bound in the external receipt.
        return AuthOnboardingMigrationExecutor.ExecuteAsync(options, fixture.MigratorConnection(name),
            "4806b71bfcfb168d53592692a935374981106af8", DateTimeOffset.UtcNow, ct, hooks);
    }

    internal async Task VerifyV20Async(CancellationToken ct)
    {
        await using var connection = await OpenOwnerAsync(ct);
        await using var database = Context(connection);
        var assembly = typeof(AuthDbContext).Assembly;
        var type = assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifestVerifier", true)!;
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(value => value.Name == "VerifyAsync"
            && value.GetParameters() is var parameters && parameters.Length == 3 && parameters[1].ParameterType == typeof(Assembly));
        await (Task)method.Invoke(null, [database, assembly, ct])!;
    }

    internal async Task AssertTargetAbsentAsync(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAdminAsync(name, ct);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"=@target)=0
              AND (SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)=@previous
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname='public' AND c.relname=ANY(@tables))
            """, connection);
        command.Parameters.AddWithValue("target", Target);
        command.Parameters.AddWithValue("previous", Previous);
        command.Parameters.AddWithValue("tables", Enrollment);
        Assert.True(await command.ExecuteScalarAsync(ct) is true, "Target/history/schema rollback was incomplete.");
    }

    internal async Task AssertCommittedAsync(CancellationToken ct)
    {
        await using (var connection = await fixture.OpenAdminAsync(name, ct))
        await using (var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"=@target)=1
              AND (SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)=@target
              AND (SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname='public' AND c.relname=ANY(@tables) AND c.relkind='r')=4
            """, connection))
        {
            command.Parameters.AddWithValue("target", Target);
            command.Parameters.AddWithValue("tables", Enrollment);
            Assert.True(await command.ExecuteScalarAsync(ct) is true, "Target history/schema was not committed exactly once.");
        }
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnection(name));
        await runtime.OpenAsync(ct);
        await using var grants = new NpgsqlCommand("""
            SELECT bool_and(pg_catalog.has_table_privilege(current_user,c.oid,'SELECT')
                AND pg_catalog.has_table_privilege(current_user,c.oid,'INSERT')
                AND NOT pg_catalog.has_table_privilege(current_user,c.oid,'UPDATE')
                AND NOT pg_catalog.has_table_privilege(current_user,c.oid,'DELETE')) AND count(*)=4
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname=ANY(@tables)
            """, runtime);
        grants.Parameters.AddWithValue("tables", Enrollment);
        Assert.True(await grants.ExecuteScalarAsync(ct) is true, "Actual runtime table grants diverged from exact source contract.");
    }

    internal async Task AddUnexpectedDefaultGrantAsync(CancellationToken ct)
    {
        await using var connection = await OpenOwnerAsync(ct);
        await ExecuteAsync(connection, "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO zeka_auth_runtime", ct);
    }

    internal async Task AssertSameAdvisoryLockWaiterAsync(int holderPid, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(3));
        await using var connection = await fixture.OpenAdminAsync(name, bound.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(
                  SELECT 1 FROM pg_catalog.pg_locks held JOIN pg_catalog.pg_locks waiting
                    ON waiting.locktype=held.locktype AND waiting.database=held.database
                    AND waiting.classid=held.classid AND waiting.objid=held.objid AND waiting.objsubid=held.objsubid
                  WHERE held.pid=@holder AND held.locktype='advisory' AND held.granted
                    AND waiting.pid<>held.pid AND NOT waiting.granted)
                """, connection) { CommandTimeout = 2 };
            command.Parameters.AddWithValue("holder", holderPid);
            if (await command.ExecuteScalarAsync(bound.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), bound.Token);
        }
    }

    internal async Task<string> DigestAsync(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAdminAsync(name, ct);
        await using var command = new NpgsqlCommand("""
            WITH state AS (
              SELECT 'relation|'||n.nspname||'|'||c.relname||'|'||c.relkind::text||'|'||c.relowner::text||'|'
                 ||coalesce(c.relacl::text,'')||'|'||c.relrowsecurity::text||'|'||c.relforcerowsecurity::text AS value
                FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname IN('public','zeka')
              UNION ALL SELECT 'column|'||n.nspname||'|'||c.relname||'|'||a.attname||'|'||pg_catalog.format_type(a.atttypid,a.atttypmod)||'|'||a.attnotnull::text||'|'||coalesce(a.attacl::text,'')
                FROM pg_catalog.pg_attribute a JOIN pg_catalog.pg_class c ON c.oid=a.attrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname IN('public','zeka') AND a.attnum>0 AND NOT a.attisdropped
              UNION ALL SELECT 'default|'||owner.rolname||'|'||coalesce(n.nspname,'')||'|'||d.defaclobjtype::text||'|'||d.defaclacl::text
                FROM pg_catalog.pg_default_acl d JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
                WHERE owner.rolname LIKE 'zeka_auth_%'
              UNION ALL SELECT 'policy|'||n.nspname||'|'||c.relname||'|'||p.polname||'|'||p.polcmd::text||'|'||p.polroles::text||'|'
                 ||coalesce(pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),'')||'|'||coalesce(pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false),'')
                FROM pg_catalog.pg_policy p JOIN pg_catalog.pg_class c ON c.oid=p.polrelid JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname IN('public','zeka')
              UNION ALL SELECT 'history|'||"MigrationId"||'|'||"ProductVersion" FROM public."__EFMigrationsHistory"
            ) SELECT value FROM state ORDER BY value
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(fixture.MigratorConnection(name));
        try
        {
            await connection.OpenAsync(ct);
            await ExecuteAsync(connection, "SET ROLE zeka_auth_owner", ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static AuthDbContext Context(NpgsqlConnection connection) => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deployments/database/bootstrap-auth-roles.sql"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("SOURCE_BOOTSTRAP_NOT_FOUND");
    }
}
