using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class RealProcessMigrationTests : IAsyncLifetime
{
    private const string MigratorPassword = "test-only-migrator-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private readonly string repository = FindRepository();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Real_process_supports_latest_exact_already_current_invalid_target_failure_and_retry()
    {
        var database = await CreateDatabaseAsync("auth_paths");
        await BootstrapAsync(database, "auth");

        var plan = await InvokeAsync(database, "auth", "plan", "20260814120613_MicrosoftIdentity");
        Assert.Equal(0, plan.ExitCode);
        Assert.Equal("FORWARD_APPROVED", plan.Classification);
        Assert.Equal("not_started", plan.MigrationStatus);

        var first = await InvokeAsync(database, "auth", "apply", "20260814120613_MicrosoftIdentity");
        Assert.Equal(0, first.ExitCode);
        Assert.Equal("FORWARD_APPROVED", first.Classification);
        Assert.Equal("passed", first.MigrationStatus);

        var exact = await InvokeAsync(database, "auth", "apply", "20260819155420_OnboardingOperationalFoundations");
        Assert.Equal(0, exact.ExitCode);
        Assert.Equal("FORWARD_APPROVED", exact.Classification);

        var latest = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(0, latest.ExitCode);
        Assert.Equal("FORWARD_APPROVED", latest.Classification);
        Assert.Equal("passed", latest.ManifestStatus);

        var beforeInvalid = await DigestAsync(database);
        var invalid = await InvokeAsync(database, "auth", "apply", "20260101000000_UnknownMigration");
        Assert.Equal(25, invalid.ExitCode);
        Assert.Equal("TARGET_REJECTED", invalid.ResultCode);
        Assert.Equal(beforeInvalid, await DigestAsync(database));

        var current = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(0, current.ExitCode);
        Assert.Equal("ALREADY_CURRENT", current.Classification);
        Assert.Equal("not_started", current.MigrationStatus);
        Assert.Equal("passed", current.ManifestStatus);

        await ExecuteAdminAsync(database, """
            SET ROLE zeka_auth_owner;
            INSERT INTO public."__EFMigrationsHistory" ("MigrationId","ProductVersion")
              VALUES ('99999999999999_UnknownAppliedMigration','8.0.30');
            RESET ROLE;
            """);
        var divergent = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(25, divergent.ExitCode);
        Assert.Equal("MIGRATION_HISTORY_REJECTED", divergent.ResultCode);

        var failedDatabase = await CreateDatabaseAsync("auth_failure");
        await BootstrapAsync(failedDatabase, "auth");
        Assert.Equal(0, (await InvokeAsync(failedDatabase, "auth", "apply",
            "20260814120613_MicrosoftIdentity")).ExitCode);
        await ExecuteAdminAsync(failedDatabase, "SET ROLE zeka_auth_owner; CREATE TABLE public.\"AuditEntries\"(id integer); RESET ROLE;");
        var failed = await InvokeAsync(failedDatabase, "auth", "apply",
            "20260819155420_OnboardingOperationalFoundations");
        Assert.Equal(28, failed.ExitCode);
        Assert.Equal("MIGRATION_STATE_REQUIRES_INSPECTION", failed.Classification);
        Assert.Equal("failed", failed.MigrationStatus);
        var retry = await InvokeAsync(failedDatabase, "auth", "apply",
            "20260819155420_OnboardingOperationalFoundations");
        Assert.Equal(28, retry.ExitCode);
        Assert.Equal("MIGRATION_STATE_REQUIRES_INSPECTION", retry.Classification);
    }

    [Fact]
    public async Task Real_process_rejects_adminarea_down_target_without_state_change()
        => await AssertDownTargetAsync("adminarea", "20260912180000_PostgreSqlRlsAndRuntimeRoles");

    [Fact]
    public async Task Real_process_rejects_auth_down_target_without_state_change()
        => await AssertDownTargetAsync("auth", "20260910165430_TenantPermissionCatalogue");

    [Fact]
    public async Task Real_process_rejects_client_down_target_without_state_change()
        => await AssertDownTargetAsync("client", "20260910165923_ExplicitTenantEnforcement");

    [Fact]
    public async Task Real_process_rejects_wrong_and_drifted_migrator_identity_and_bounded_lock_contention()
    {
        var database = await PrepareLatestAsync("auth", "identity");
        var administrator = AdminConnection(database);
        var wrong = await InvokeAsync(database, "auth", "apply", "latest", administrator);
        Assert.Equal(24, wrong.ExitCode);
        Assert.Equal("IDENTITY_REJECTED", wrong.ResultCode);

        await ExecuteAdminAsync(database, "REVOKE zeka_auth_owner FROM zeka_auth_migrator;");
        var missing = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(24, missing.ExitCode);
        await BootstrapAsync(database, "auth");

        var clientDatabase = await CreateDatabaseAsync("client_roles");
        await BootstrapAsync(clientDatabase, "client");
        await ExecuteAdminAsync(database, "GRANT zeka_client_owner TO zeka_auth_migrator WITH INHERIT FALSE, SET TRUE;");
        var extra = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(24, extra.ExitCode);
        await ExecuteAdminAsync(database, "REVOKE zeka_client_owner FROM zeka_auth_migrator; REVOKE zeka_auth_owner FROM zeka_auth_migrator; GRANT zeka_client_owner TO zeka_auth_migrator WITH INHERIT FALSE, SET TRUE;");
        var crossServiceOnly = await InvokeAsync(database, "auth", "apply", "latest");
        Assert.Equal(24, crossServiceOnly.ExitCode);
        await ExecuteAdminAsync(database, "REVOKE zeka_client_owner FROM zeka_auth_migrator;");
        await BootstrapAsync(database, "auth");

        await using var lockConnection = new NpgsqlConnection(MigratorConnection(database, "auth"));
        await lockConnection.OpenAsync();
        await using (var lockCommand = new NpgsqlCommand("""
            SELECT pg_catalog.pg_advisory_lock(
              1515870810,
              CASE WHEN oid::bigint > 2147483647 THEN (oid::bigint - 4294967296)::integer ELSE oid::integer END)
            FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()
            """, lockConnection))
            await lockCommand.ExecuteNonQueryAsync();
        var contention = await InvokeAsync(database, "auth", "apply", "latest", lockTimeoutSeconds: 1);
        Assert.Equal(23, contention.ExitCode);
        Assert.Equal("LOCK_UNAVAILABLE", contention.ResultCode);
    }

    [Fact]
    public async Task Real_process_rejects_output_escape_link_and_overwrite_attacks_without_touching_external_targets()
    {
        var root = NewEvidenceRoot("output-attacks");
        var external = Path.Combine(Path.GetTempPath(), "zeka-db-migrate-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var externalFile = Path.Combine(external, "protected.txt");
        await File.WriteAllTextAsync(externalFile, "unchanged", Encoding.UTF8);
        var sentinelHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(externalFile)));
        try
        {
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "../escape.json")).ExitCode);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, Path.Combine(external, "absolute.json"))).ExitCode);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "C:\\device\\escape.json")).ExitCode);

            var linkedParent = Path.Combine(root, "linked-parent");
            Directory.CreateSymbolicLink(linkedParent, external);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "linked-parent/created.json")).ExitCode);
            Assert.False(File.Exists(Path.Combine(external, "created.json")));

            var finalLink = Path.Combine(root, "final.json");
            File.CreateSymbolicLink(finalLink, externalFile);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "final.json")).ExitCode);
            File.Delete(finalLink);
            File.CreateSymbolicLink(finalLink, Path.Combine(external, "missing.json"));
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "final.json")).ExitCode);
            File.Delete(finalLink);

            var existing = Path.Combine(root, "existing.json");
            await File.WriteAllTextAsync(existing, "existing", Encoding.UTF8);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(root, "existing.json")).ExitCode);
            Assert.Equal("existing", await File.ReadAllTextAsync(existing, Encoding.UTF8));

            var rootLink = root + "-link";
            Directory.CreateSymbolicLink(rootLink, root);
            Assert.Equal(29, (await InvokeWithoutCredentialAsync(rootLink, "root-link.json")).ExitCode);
            Directory.Delete(rootLink);
            Assert.Equal(sentinelHash, Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(externalFile))));
            Assert.Equal(["protected.txt"], Directory.GetFiles(external).Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            if (Directory.Exists(external)) Directory.Delete(external, true);
        }
    }

    [Fact]
    public async Task Real_process_atomically_publishes_one_complete_sanitized_result_inside_authorized_root()
    {
        var root = NewEvidenceRoot("atomic-output");
        var result = await InvokeWithoutCredentialAsync(root, "nested/result.json", createParent: true);
        Assert.Equal(21, result.ExitCode);
        var path = Path.Combine(root, "nested", "result.json");
        Assert.True(File.Exists(path));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal("CREDENTIAL_REJECTED", document.RootElement.GetProperty("resultCode").GetString());
        Assert.DoesNotContain("password", await File.ReadAllTextAsync(path), StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "nested")));
    }

    private async Task AssertDownTargetAsync(string service, string olderTarget)
    {
        var database = await PrepareLatestAsync(service, "ops03");
        var before = await DigestAsync(database);
        var result = await InvokeAsync(database, service, "apply", olderTarget);
        Assert.Equal(26, result.ExitCode);
        Assert.Equal("DOWN_TARGET_REJECTED", result.Classification);
        Assert.Equal(ExpectedMigrator(service), result.SessionUser);
        Assert.Equal(ExpectedOwner(service), result.CurrentUser);
        Assert.True(result.OwnerAssumption);
        Assert.Equal("not_started", result.MigrationStatus);
        Assert.Equal("not_started", result.BootstrapStatus);
        Assert.Equal(before, await DigestAsync(database));
        await WriteDigestArtifactAsync(service, result.OperationId, before);
    }

    private async Task<string> PrepareLatestAsync(string service, string suffix)
    {
        var database = await CreateDatabaseAsync(service + "_" + suffix);
        await BootstrapAsync(database, service);
        if (service == "adminarea")
        {
            Assert.Equal(0, (await InvokeAsync(database, service, "apply", "20250427103057_Initial Migration")).ExitCode);
            var organisation = Guid.NewGuid();
            await ExecuteAdminAsync(database, $"""
                SET ROLE zeka_adminarea_owner;
                UPDATE public."StaffMembers" SET "UserName"='jdoe' WHERE "Id"=1;
                CREATE TABLE public."__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
                INSERT INTO public."__OrganisationTenantMap" VALUES ('Zeka','{organisation:D}');
                RESET ROLE;
                """);
            Assert.Equal(0, (await InvokeAsync(database, service, "apply",
                "20260904124303_OrganisationTenantConstraints")).ExitCode);
            await ExecuteAdminAsync(database, $"""
                SET ROLE zeka_adminarea_owner;
                CREATE TABLE public."__StaffMembershipMap" ("LocalId" integer PRIMARY KEY, "OrganisationId" uuid NOT NULL, "OrganisationMembershipId" uuid NOT NULL);
                INSERT INTO public."__StaffMembershipMap" VALUES
                  (1,'{organisation:D}','{Guid.NewGuid():D}'),(2,'{organisation:D}','{Guid.NewGuid():D}'),(3,'{organisation:D}','{Guid.NewGuid():D}');
                RESET ROLE;
                """);
        }
        else if (service == "client")
        {
            await ExecuteAdminAsync(database, """
                SET ROLE zeka_client_owner;
                CREATE TABLE public."__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
                RESET ROLE;
                """);
        }
        var latest = await InvokeAsync(database, service, "apply", "latest");
        Assert.Equal(0, latest.ExitCode);
        Assert.Equal("passed", latest.ManifestStatus);
        return database;
    }

    private async Task<string> CreateDatabaseAsync(string label)
    {
        var candidate = "issue46_" + label + "_" + Guid.NewGuid().ToString("N");
        var database = candidate[..Math.Min(candidate.Length, 55)];
        await ExecuteAdminAsync(null, $"CREATE DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(database)}");
        return database;
    }

    private async Task BootstrapAsync(string database, string service)
    {
        var sql = await File.ReadAllTextAsync(Path.Combine(repository, "Deployments", "database",
            $"bootstrap-{service}-roles.sql"));
        await ExecuteAdminAsync(database, sql);
        await ExecuteAdminAsync(database,
            $"ALTER ROLE {ExpectedMigrator(service)} PASSWORD '{MigratorPassword}';");
    }

    private async Task ExecuteAdminAsync(string? database, string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnection(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> DigestAsync(string database)
    {
        await using var connection = new NpgsqlConnection(AdminConnection(database));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            WITH state AS (
              SELECT 'role|'||rolname||'|'||rolcanlogin||'|'||rolsuper||'|'||rolbypassrls||'|'||rolcreatedb||'|'||rolcreaterole||'|'||rolinherit||'|'||rolreplication||'|'||coalesce(array_to_string(rolconfig,','),'') value
                FROM pg_catalog.pg_roles WHERE rolname LIKE 'zeka\_%' ESCAPE '\'
              UNION ALL SELECT 'member|'||member.rolname||'|'||parent.rolname||'|'||m.admin_option||'|'||m.inherit_option||'|'||m.set_option
                FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles member ON member.oid=m.member JOIN pg_catalog.pg_roles parent ON parent.oid=m.roleid
                WHERE member.rolname LIKE 'zeka\_%' ESCAPE '\' OR parent.rolname LIKE 'zeka\_%' ESCAPE '\'
              UNION ALL SELECT 'setting|'||role.rolname||'|'||setting.setdatabase||'|'||array_to_string(setting.setconfig,',')
                FROM pg_catalog.pg_db_role_setting setting JOIN pg_catalog.pg_roles role ON role.oid=setting.setrole WHERE role.rolname LIKE 'zeka\_%' ESCAPE '\'
              UNION ALL SELECT 'defaultacl|'||owner.rolname||'|'||coalesce(namespace.nspname,'')||'|'||acl.defaclobjtype::text||'|'||coalesce(acl.defaclacl::text,'')
                FROM pg_catalog.pg_default_acl acl JOIN pg_catalog.pg_roles owner ON owner.oid=acl.defaclrole LEFT JOIN pg_catalog.pg_namespace namespace ON namespace.oid=acl.defaclnamespace
                WHERE owner.rolname LIKE 'zeka\_%' ESCAPE '\'
              UNION ALL SELECT 'object|'||namespace.nspname||'|'||class.relname||'|'||class.relkind::text||'|'||owner.rolname||'|'||coalesce(class.relacl::text,'')||'|'||class.relrowsecurity||'|'||class.relforcerowsecurity
                FROM pg_catalog.pg_class class JOIN pg_catalog.pg_namespace namespace ON namespace.oid=class.relnamespace JOIN pg_catalog.pg_roles owner ON owner.oid=class.relowner
                WHERE namespace.nspname IN ('public','zeka')
              UNION ALL SELECT 'schema|'||namespace.nspname||'|'||owner.rolname||'|'||coalesce(namespace.nspacl::text,'')
                FROM pg_catalog.pg_namespace namespace JOIN pg_catalog.pg_roles owner ON owner.oid=namespace.nspowner WHERE namespace.nspname IN ('public','zeka')
              UNION ALL SELECT 'policy|'||namespace.nspname||'|'||class.relname||'|'||policy.polname||'|'||policy.polcmd::text||'|'||policy.polpermissive||'|'||coalesce(pg_catalog.pg_get_expr(policy.polqual,policy.polrelid,false),'')||'|'||coalesce(pg_catalog.pg_get_expr(policy.polwithcheck,policy.polrelid,false),'')||'|'||policy.polroles::text
                FROM pg_catalog.pg_policy policy JOIN pg_catalog.pg_class class ON class.oid=policy.polrelid JOIN pg_catalog.pg_namespace namespace ON namespace.oid=class.relnamespace WHERE namespace.nspname IN ('public','zeka')
              UNION ALL SELECT 'history|'||"MigrationId" FROM public."__EFMigrationsHistory"
            ) SELECT value FROM state ORDER BY value
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var builder = new StringBuilder();
        while (await reader.ReadAsync()) builder.AppendLine(reader.GetString(0));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private async Task WriteDigestArtifactAsync(string service, Guid operationId, string digest)
    {
        var root = Path.Combine(repository, ".artifacts", "issue46", "ops03", "digests");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, $"{service}-{operationId:D}.sha256"), digest + Environment.NewLine);
    }

    private async Task<CliResult> InvokeAsync(string database, string service, string operation, string target,
        string? credential = null, int lockTimeoutSeconds = 5)
    {
        var operationId = Guid.NewGuid();
        var root = NewEvidenceRoot(Path.Combine("real-process", service));
        var file = $"{operationId:D}.json";
        var process = Start(operation, service, target, operationId, root, file, lockTimeoutSeconds);
        await process.StandardInput.WriteLineAsync(credential ?? MigratorConnection(database, service));
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        _ = await stdout;
        _ = await stderr;
        var resultPath = Path.Combine(root, file);
        Assert.True(File.Exists(resultPath));
        return CliResult.Read(process.ExitCode, resultPath);
    }

    private async Task<(int ExitCode, string StandardError)> InvokeWithoutCredentialAsync(string root, string file,
        bool createParent = false)
    {
        if (createParent) Directory.CreateDirectory(Path.Combine(root, Path.GetDirectoryName(file)!));
        var process = Start("apply", "auth", "latest", Guid.NewGuid(), root, file, 1);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await error);
    }

    private Process Start(string operation, string service, string target, Guid operationId, string root, string file,
        int lockTimeoutSeconds)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repository
        };
        info.ArgumentList.Add(Path.Combine(repository, "Tools", "Zeka.DbMigrate", "bin", "Release", "net8.0",
            "zeka-db-migrate.dll"));
        foreach (var argument in new[]
                 {
                     operation, "--service", service, "--target", target, "--operation-id", operationId.ToString("D"),
                     "--evidence-root", root, "--evidence-file", file, "--lock-timeout-seconds",
                     lockTimeoutSeconds.ToString(), "--credential-stdin"
                 }) info.ArgumentList.Add(argument);
        return Process.Start(info) ?? throw new InvalidOperationException("Could not start zeka-db-migrate.");
    }

    private string NewEvidenceRoot(string label)
    {
        var root = Path.Combine(repository, ".artifacts", "issue46", "ops03", label,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private string AdminConnection(string? database) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
    { Database = database ?? new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database }.ConnectionString;

    private string MigratorConnection(string database, string service) => new NpgsqlConnectionStringBuilder(AdminConnection(database))
    { Username = ExpectedMigrator(service), Password = MigratorPassword, Pooling = false }.ConnectionString;

    private static string ExpectedMigrator(string service) => $"zeka_{service}_migrator";
    private static string ExpectedOwner(string service) => $"zeka_{service}_owner";

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "zeka-web-api.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record CliResult(int ExitCode, Guid OperationId, string Classification, string ResultCode,
        string? SessionUser, string? CurrentUser, bool OwnerAssumption, string MigrationStatus, string BootstrapStatus,
        string ManifestStatus)
    {
        public static CliResult Read(int exitCode, string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var roles = root.GetProperty("roles");
            var phases = root.GetProperty("phases");
            return new(exitCode, root.GetProperty("operationId").GetGuid(),
                root.GetProperty("classification").GetString()!, root.GetProperty("resultCode").GetString()!,
                roles.GetProperty("observedSessionUser").GetString(), roles.GetProperty("observedCurrentUser").GetString(),
                roles.GetProperty("ownerAssumption").GetBoolean(),
                phases.GetProperty("migration").GetProperty("status").GetString()!,
                phases.GetProperty("bootstrapReconciliation").GetProperty("status").GetString()!,
                phases.GetProperty("manifestVerification").GetProperty("status").GetString()!);
        }
    }
}
