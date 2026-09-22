using System.Collections.Concurrent;
using System.Data;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

public sealed class PostgreSqlRlsRuntimeDatabase : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string RuntimePassword = "test-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    private int nextPartnerNumber = 9000;

    public string RuntimeConnectionString => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
    {
        Username = "zeka_adminarea_runtime",
        Password = RuntimePassword,
        MaxPoolSize = 2,
        MinPoolSize = 0,
        NoResetOnClose = false
    }.ConnectionString;

    public string SingleConnectionRuntimeString => new NpgsqlConnectionStringBuilder(RuntimeConnectionString)
    {
        MaxPoolSize = 1
    }.ConnectionString;

    public string MigratorConnectionString => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
    {
        Username = "zeka_adminarea_migrator",
        Password = MigratorPassword
    }.ConnectionString;

    public string DatabaseName => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!;

    public string AdministratorConnectionString => postgres.GetConnectionString();
    public Guid SeedOrganisation { get; private set; }

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await ExecuteAdministratorAsync(ReadBootstrapScript());
        await ExecuteAdministratorAsync($"ALTER ROLE zeka_adminarea_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_adminarea_runtime PASSWORD '{RuntimePassword}';");
        await ApplyAllMigrationsAsMigratorAsync();
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    public async Task SeedTeamAsAdministratorAsync(Guid organisation, string name)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, """
            INSERT INTO "Teams" ("Name", "Acronym", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "OrganisationId")
            VALUES (@name, @name, 'test', now(), 'test', false, @organisation)
            """, new NpgsqlParameter("name", name), new NpgsqlParameter("organisation", organisation));
    }

    public async Task<int> SeedPartnerAsAdministratorAsync(Guid organisation, string name)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."Partners"
              ("PartnerNumber", "Name", "Address_Number", "Address_Street", "Address_PostalCode",
               "Address_City", "StaffMemberId", "CategoryOfPartner", "CategoryOfPartnerName",
               "StatusOfPartner", "DateOfAgreementSignature", "IsEconomieSociale", "CreatedBy",
               "Created", "LastModifiedBy", "Softdelete", "OrganisationId")
            VALUES
              (@number, @name, '1', 'Evidence Street', '1000', 'Brussels', 1, 0, 'Evidence',
               0, current_date, false, 'test', now(), 'test', false, @organisation)
            RETURNING "Id"
            """, connection);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("number", Interlocked.Increment(ref nextPartnerNumber));
        command.Parameters.AddWithValue("organisation", organisation);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task ApplyAllMigrationsAsMigratorAsync()
    {
        await using var deployment = Deployment(MigratorConnectionString);
        await deployment.Database.OpenConnectionAsync();
        await deployment.Database.ExecuteSqlRawAsync("SET ROLE zeka_adminarea_owner");
        var migrator = deployment.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisation = Guid.NewGuid();
        SeedOrganisation = organisation;
        await deployment.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "StaffMembers" SET "UserName" = 'jdoe' WHERE "Id" = 1;
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {organisation});
            """);
        await migrator.MigrateAsync("20260904124303_OrganisationTenantConstraints");
        await deployment.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__StaffMembershipMap" ("LocalId" integer PRIMARY KEY, "OrganisationId" uuid NOT NULL, "OrganisationMembershipId" uuid NOT NULL);
            INSERT INTO "__StaffMembershipMap" VALUES
              (1, {organisation}, {Guid.NewGuid()}), (2, {organisation}, {Guid.NewGuid()}), (3, {organisation}, {Guid.NewGuid()});
            """);
        await migrator.MigrateAsync("20260912180000_PostgreSqlRlsAndRuntimeRoles");
        var pending = (await deployment.Database.GetPendingMigrationsAsync()).ToArray();
        if (!pending.SequenceEqual(["20260917212648_DurableDocumentFileOperations"], StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Supported upgrade checkpoint drifted: [{string.Join(", ", pending)}].");
        await migrator.MigrateAsync();
        if ((await deployment.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("Supported upgrade did not reach the reviewed latest migration.");
    }

    private static DeploymentDbContext Deployment(string connectionString) => new(
        new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString).Options);

    public async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, sql);
    }

    public Task ReapplyBootstrapAsync() => ExecuteAdministratorAsync(ReadBootstrapScript());

    public async Task<string> RequireBackupToolAsync(string executable)
    {
        if (executable is not ("pg_dump" or "pg_restore"))
            throw new ArgumentOutOfRangeException(nameof(executable));
        var result = await postgres.ExecAsync([executable, "--version"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{executable} prerequisite failed: {result.Stderr}");
        return result.Stdout.Trim();
    }

    public Task<(string Stdout, string Stderr)> GetLogsAsync(DateTime since, DateTime until,
        CancellationToken cancellationToken = default) =>
        postgres.GetLogsAsync(since, until, false, cancellationToken);

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadBootstrapScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-adminarea-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed AdminArea role bootstrap script was not found.");
    }
}

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class PostgreSqlRlsRuntimeEvidenceTests(PostgreSqlRlsRuntimeDatabase database, ITestOutputHelper output)
    : IClassFixture<PostgreSqlRlsRuntimeDatabase>
{
    private static readonly string[] ProtectedTables =
    [
        "ContactPersons",
        "DocumentPartners",
        "Emails",
        "Partners",
        "StaffMembers",
        "StaffProjectionOutbox",
        "Teams"
    ];

    [Fact]
    public async Task Supported_upgrade_checkpoints_end_at_the_reviewed_latest_migration()
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var applied = new List<string>();
        while (await reader.ReadAsync()) applied.Add(reader.GetString(0));
        Assert.Contains("20260912180000_PostgreSqlRlsAndRuntimeRoles", applied);
        Assert.Equal("20260917212648_DurableDocumentFileOperations", applied[^1]);
    }

    [Fact]
    public async Task Database_wide_backup_restore_prerequisite_tools_are_available_and_version_aligned()
    {
        var dump = await database.RequireBackupToolAsync("pg_dump");
        var restore = await database.RequireBackupToolAsync("pg_restore");
        Assert.StartsWith("pg_dump (PostgreSQL) 17.", dump, StringComparison.Ordinal);
        Assert.StartsWith("pg_restore (PostgreSQL) 17.", restore, StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_runbook_covers_required_incidents_without_ADR_008_commitments()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? runbook = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database",
                "production-conformance-runbook.md");
            if (File.Exists(candidate))
            {
                runbook = File.ReadAllText(candidate);
                break;
            }
            directory = directory.Parent;
        }

        Assert.NotNull(runbook);
        Assert.Contains("## Runtime identity mismatch", runbook, StringComparison.Ordinal);
        Assert.Contains("## Catalog drift", runbook, StringComparison.Ordinal);
        Assert.Contains("## Pool-contamination suspicion", runbook, StringComparison.Ordinal);
        Assert.Contains("## Migration or bootstrap failure", runbook, StringComparison.Ordinal);
        Assert.Contains("RTO, RPO, backup retention, regional recovery and tenant movement remain ADR-008-blocked",
            runbook, StringComparison.Ordinal);
        Assert.Contains("Per-tenant restoration requires logical reconstruction and reconciliation",
            runbook, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_role_is_restricted_and_every_protected_table_is_forced_with_targeted_policy()
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using (var command = new NpgsqlCommand("""
            SELECT rolsuper, rolbypassrls, rolcreatedb, rolcreaterole, rolinherit
            FROM pg_roles WHERE rolname = current_user
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.False(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
        }

        await using (var command = new NpgsqlCommand("""
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity, owner.rolname,
                   p.polname, pg_get_expr(p.polqual, p.polrelid), pg_get_expr(p.polwithcheck, p.polrelid)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_roles owner ON owner.oid = c.relowner
            LEFT JOIN pg_policy p ON p.polrelid = c.oid
            WHERE n.nspname = 'public' AND c.relname = ANY(@tables)
            ORDER BY c.relname
            """, connection))
        {
            command.Parameters.AddWithValue("tables", ProtectedTables);
            await using var reader = await command.ExecuteReaderAsync();
            var observed = new List<string>();
            while (await reader.ReadAsync())
            {
                observed.Add(reader.GetString(0));
                Assert.True(reader.GetBoolean(1));
                Assert.True(reader.GetBoolean(2));
                Assert.Equal("zeka_adminarea_owner", reader.GetString(3));
                Assert.Equal($"rls_{reader.GetString(0).ToLowerInvariant()}_organisation", reader.GetString(4));
                Assert.Contains("zeka.current_organisation_id()", reader.GetString(5));
                Assert.Contains("zeka.current_organisation_id()", reader.GetString(6));
            }
            Assert.Equal(ProtectedTables.Order(), observed);
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("CREATE TABLE forbidden_runtime_ddl(id int)", connection);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    [Fact]
    public async Task Startup_identity_validation_rejects_privileged_session_masked_as_runtime_role()
    {
        const string expectedRole = "zeka_adminarea_runtime";
        var administratorRole = new NpgsqlConnectionStringBuilder(database.AdministratorConnectionString).Username!;

        await ValidateRuntimeIdentityAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeDatabaseIdentityValidator(database.AdministratorConnectionString, expectedRole)
                .StartAsync(default));

        var maskedAdministrator = new NpgsqlConnectionStringBuilder(database.AdministratorConnectionString)
        {
            Options = $"-c role={expectedRole}",
            Pooling = false
        }.ConnectionString;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeDatabaseIdentityValidator(maskedAdministrator, expectedRole).StartAsync(default));

        await using var connection = new NpgsqlConnection(maskedAdministrator);
        await connection.OpenAsync();
        await using (var maskedIdentity = new NpgsqlCommand("select session_user,current_user", connection))
        await using (var reader = await maskedIdentity.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(administratorRole, reader.GetString(0));
            Assert.Equal(expectedRole, reader.GetString(1));
        }

        await using (var reset = new NpgsqlCommand("set role none", connection))
            await reset.ExecuteNonQueryAsync();
        await using (var restoredAuthority = new NpgsqlCommand("""
            select current_user,r.rolsuper,r.rolbypassrls,
                   (select count(*) from public."Teams")
            from pg_roles r where r.rolname=current_user
            """, connection))
        await using (var reader = await restoredAuthority.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(administratorRole, reader.GetString(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetInt64(3) >= 0);
        }

        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Startup_identity_validation_is_independent_of_hostile_search_path_shadowing()
    {
        await database.ExecuteAdministratorAsync("""
            CREATE SCHEMA issue45_identity_shadow;
            GRANT USAGE ON SCHEMA issue45_identity_shadow TO zeka_adminarea_runtime;
            CREATE VIEW issue45_identity_shadow.pg_roles AS
              SELECT 'zeka_adminarea_runtime'::name AS rolname, 42::oid AS oid,
                     true AS rolcanlogin, true AS rolsuper, true AS rolbypassrls,
                     true AS rolcreatedb, true AS rolcreaterole, true AS rolinherit,
                     true AS rolreplication, ARRAY['unexpected']::text[] AS rolconfig;
            CREATE VIEW issue45_identity_shadow.pg_auth_members AS
              SELECT 42::oid AS member, 42::oid AS roleid;
            CREATE VIEW issue45_identity_shadow.pg_db_role_setting AS
              SELECT 42::oid AS setrole;
            CREATE VIEW issue45_identity_shadow.pg_parameter_acl AS
              SELECT 'session_replication_role'::text AS parname;
            CREATE FUNCTION issue45_identity_shadow.has_parameter_privilege(name, text, text)
              RETURNS boolean LANGUAGE sql IMMUTABLE AS 'SELECT true';
            GRANT SELECT ON ALL TABLES IN SCHEMA issue45_identity_shadow TO zeka_adminarea_runtime;
            GRANT EXECUTE ON FUNCTION issue45_identity_shadow.has_parameter_privilege(name, text, text)
              TO zeka_adminarea_runtime;
            """);
        try
        {
            var hostileConnection = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
            {
                Options = "-c search_path=issue45_identity_shadow,public,pg_catalog",
                Pooling = false
            }.ConnectionString;

            await using (var connection = new NpgsqlConnection(hostileConnection))
            {
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand(
                    "select rolsuper,has_parameter_privilege(current_user,'session_replication_role','SET') " +
                    "from pg_roles where rolname=current_user", connection);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.True(reader.GetBoolean(0));
                Assert.True(reader.GetBoolean(1));
            }

            await new RuntimeDatabaseIdentityValidator(hostileConnection, "zeka_adminarea_runtime")
                .StartAsync(default);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("DROP SCHEMA issue45_identity_shadow CASCADE");
        }

        await ValidateRuntimeIdentityAsync();
        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Versioned_manifest_matches_EF_classification_and_effective_catalog_bidirectionally()
    {
        var manifest = RlsSecurityManifestVerifier.Load(typeof(ApplicationDbContext).Assembly);
        Assert.Equal(10, manifest.SchemaVersion);
        Assert.Equal(["FOREIGN_TABLE", "MATERIALIZED_VIEW", "VIEW"], manifest.ProhibitedRelationKinds);
        Assert.Empty(manifest.RelationTopology);
        Assert.Equal("(\"OrganisationId\" = zeka.current_organisation_id())",
            manifest.PolicyUsingExpression);
        Assert.Equal(manifest.PolicyUsingExpression, manifest.PolicyWithCheckExpression);
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(database.MigratorConnectionString).Options);
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
    }

    [Fact]
    public async Task Migration_history_schema_is_taken_from_the_manifest_when_it_is_not_the_first_managed_schema()
    {
        var manifest = RlsSecurityManifestVerifier.Load(typeof(ApplicationDbContext).Assembly) with
        {
            MigrationHistoryTable = new ManagedObjectIdentity("zeka", "__EFMigrationsHistory")
        };
        Assert.NotEqual(manifest.ManagedSchemas[0], manifest.MigrationHistoryTable.Schema);
        await database.ExecuteAdministratorAsync("""
            ALTER TABLE public."__EFMigrationsHistory" SET SCHEMA zeka;
            REVOKE USAGE ON SCHEMA public FROM zeka_adminarea_migrator;
            GRANT USAGE ON SCHEMA zeka TO zeka_adminarea_migrator;
            """);
        try
        {
            await using var deployment = new DeploymentDbContext(
                new DbContextOptionsBuilder<DeploymentDbContext>()
                    .UseNpgsql(database.MigratorConnectionString).Options);
            await RlsSecurityManifestVerifier.VerifyMigrationHistoryPlacementAsync(deployment, manifest);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                ALTER TABLE zeka."__EFMigrationsHistory" SET SCHEMA public;
                REVOKE USAGE ON SCHEMA zeka FROM zeka_adminarea_migrator;
                GRANT USAGE ON SCHEMA public TO zeka_adminarea_migrator;
                """);
        }
        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Catalog_verification_is_independent_of_hostile_search_path_shadowing()
    {
        await database.ExecuteAdministratorAsync("""
            CREATE SCHEMA issue45_catalog_shadow;
            CREATE VIEW issue45_catalog_shadow.pg_roles AS SELECT 'shadow'::name AS rolname;
            """);
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(database.MigratorConnectionString)
            {
                Options = "-c search_path=issue45_catalog_shadow,public,pg_catalog"
            }.ConnectionString;
            await using var deployment = new DeploymentDbContext(
                new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connection).Options);
            await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("DROP SCHEMA issue45_catalog_shadow CASCADE");
        }
    }

    [Fact]
    public async Task Bootstrap_rerun_clears_all_managed_settings_and_restores_only_manifest_approved_objects()
    {
        var databaseIdentifier = new NpgsqlCommandBuilder().QuoteIdentifier(database.DatabaseName);
        await database.ExecuteAdministratorAsync($"""
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE public.issue45_unknown_admin(id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
            RESET ROLE;
            GRANT ALL ON TABLE public.issue45_unknown_admin TO zeka_adminarea_runtime;
            GRANT ALL ON SEQUENCE public.issue45_unknown_admin_id_seq TO zeka_adminarea_runtime;
            GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO zeka_adminarea_runtime;
            ALTER ROLE zeka_adminarea_owner IN DATABASE {databaseIdentifier} SET application_name='unexpected';
            ALTER ROLE zeka_adminarea_migrator IN DATABASE {databaseIdentifier} SET application_name='unexpected';
            ALTER ROLE zeka_adminarea_runtime IN DATABASE {databaseIdentifier} SET application_name='unexpected';
            """);

        await database.ReapplyBootstrapAsync();

        Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
            SELECT
              (SELECT count(*)=0 FROM pg_catalog.pg_db_role_setting s
               JOIN pg_catalog.pg_roles r ON r.oid=s.setrole
               JOIN pg_catalog.pg_database d ON d.oid=s.setdatabase
               WHERE d.datname=pg_catalog.current_database()
                 AND r.rolname=ANY(ARRAY['zeka_adminarea_owner','zeka_adminarea_migrator','zeka_adminarea_runtime']))
              AND pg_catalog.has_table_privilege('zeka_adminarea_runtime','public."Teams"','SELECT')
              AND pg_catalog.has_table_privilege('zeka_adminarea_runtime','public."Teams"','INSERT')
              AND pg_catalog.has_table_privilege('zeka_adminarea_runtime','public."Teams"','UPDATE')
              AND pg_catalog.has_table_privilege('zeka_adminarea_runtime','public."Teams"','DELETE')
              AND NOT pg_catalog.has_table_privilege('zeka_adminarea_runtime','public."__EFMigrationsHistory"','SELECT')
              AND NOT pg_catalog.has_table_privilege('zeka_adminarea_runtime','public.issue45_unknown_admin','SELECT')
              AND NOT pg_catalog.has_sequence_privilege('zeka_adminarea_runtime','public.issue45_unknown_admin_id_seq','USAGE')
            """));
        await database.ExecuteAdministratorAsync("DROP TABLE public.issue45_unknown_admin");
        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Policy_comparison_preserves_quoted_identifier_identity()
    {
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();
        await SeedTeamAsync(organisationB, "PolB");
        await VerifyCatalogAsync();

        await database.ExecuteAdministratorAsync($"""
            ALTER TABLE public."Teams" ADD COLUMN "Organisation Id" uuid;
            UPDATE public."Teams" SET "Organisation Id" = '{organisationA:D}'
              WHERE "OrganisationId" = '{organisationB:D}';
            ALTER POLICY rls_teams_organisation ON public."Teams"
              USING ("Organisation Id" = zeka.current_organisation_id())
              WITH CHECK ("Organisation Id" = zeka.current_organisation_id());
            """);
        try
        {
            var emitted = await ExecuteAdministratorScalarAsync<string>("""
                SELECT pg_get_expr(p.polqual,p.polrelid)
                FROM pg_policy p
                WHERE p.polrelid='public."Teams"'::regclass
                  AND p.polname='rls_teams_organisation'
                """);
            Assert.Equal("(\"Organisation Id\" = zeka.current_organisation_id())", emitted);

            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("RLS policy definition drifted", drift.Message, StringComparison.Ordinal);

            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, organisationA);
            await using var exposed = new NpgsqlCommand("""
                SELECT count(*) FROM public."Teams"
                WHERE "OrganisationId"=@organisation
                """, connection, transaction);
            exposed.Parameters.AddWithValue("organisation", organisationB);
            Assert.Equal(1L, await exposed.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                ALTER TABLE public."Teams" DROP COLUMN "Organisation Id";
                DELETE FROM public."Teams" WHERE "Name"='PolB';
                """);
        }

        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Policy_comparison_accepts_only_PostgreSql_canonical_presentation_and_rejects_semantic_drift()
    {
        const string expected = "(\"OrganisationId\" = zeka.current_organisation_id())";
        const string restore = """
            ALTER POLICY rls_teams_organisation ON public."Teams"
              USING ("OrganisationId" = zeka.current_organisation_id())
              WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
            """;
        await VerifyCatalogAsync();
        Assert.Equal($"{expected}|{expected}", await ReadTeamPolicyExpressionsAsync());

        await database.ExecuteAdministratorAsync("""
            ALTER POLICY rls_teams_organisation ON public."Teams"
              USING ((( "OrganisationId"=zeka.current_organisation_id() )))
              WITH CHECK (((("OrganisationId" = zeka.current_organisation_id()))));
            """);
        await VerifyCatalogAsync();
        Assert.Equal($"{expected}|{expected}", await ReadTeamPolicyExpressionsAsync());
        await database.ExecuteAdministratorAsync(restore);

        var cases = new (string Mutation, string Cleanup)[]
        {
            ("""
                ALTER TABLE public."Teams" ADD COLUMN "organisationid" uuid;
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("organisationid" = zeka.current_organisation_id())
                  WITH CHECK ("organisationid" = zeka.current_organisation_id());
                """, "ALTER TABLE public.\"Teams\" DROP COLUMN \"organisationid\";"),
            ("""
                CREATE FUNCTION public.issue45_policy_context() RETURNS uuid
                  LANGUAGE sql STABLE AS 'SELECT zeka.current_organisation_id()';
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" = public.issue45_policy_context())
                  WITH CHECK ("OrganisationId" = public.issue45_policy_context());
                """, "DROP FUNCTION public.issue45_policy_context();"),
            ("""
                CREATE FUNCTION zeka.issue45_current_organisation_id() RETURNS uuid
                  LANGUAGE sql STABLE AS 'SELECT zeka.current_organisation_id()';
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" = zeka.issue45_current_organisation_id())
                  WITH CHECK ("OrganisationId" = zeka.issue45_current_organisation_id());
                """, "DROP FUNCTION zeka.issue45_current_organisation_id();"),
            ("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" IS NOT DISTINCT FROM zeka.current_organisation_id())
                  WITH CHECK ("OrganisationId" IS NOT DISTINCT FROM zeka.current_organisation_id());
                """, string.Empty),
            ("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING (("OrganisationId" = zeka.current_organisation_id()) AND true)
                  WITH CHECK (("OrganisationId" = zeka.current_organisation_id()) AND true);
                """, string.Empty),
            ("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING (true)
                  WITH CHECK ("OrganisationId" = zeka.current_organisation_id());
                """, string.Empty),
            ("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" = zeka.current_organisation_id())
                  WITH CHECK (true);
                """, string.Empty),
            ("""
                ALTER POLICY rls_teams_organisation ON public."Teams"
                  USING ("OrganisationId" = '11111111-1111-1111-1111-111111111111'::uuid)
                  WITH CHECK ("OrganisationId" = '11111111-1111-1111-1111-111111111111'::uuid);
                """, string.Empty)
        };

        foreach (var (mutation, cleanup) in cases)
        {
            await database.ExecuteAdministratorAsync(mutation);
            try
            {
                var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
                Assert.Contains("RLS policy definition drifted", drift.Message, StringComparison.Ordinal);
            }
            finally
            {
                await database.ExecuteAdministratorAsync(restore + cleanup);
            }
            await VerifyCatalogAsync();
        }
    }

    [Fact]
    public async Task Undeclared_privileged_view_is_rejected_and_restoration_returns_green()
    {
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();
        await database.SeedTeamAsAdministratorAsync(organisationA, "ViewA");
        await database.SeedTeamAsAdministratorAsync(organisationB, "ViewB");
        await VerifyCatalogAsync();

        await database.ExecuteAdministratorAsync("""
            CREATE VIEW public.issue45_review_view AS
              SELECT "OrganisationId" FROM public."Teams";
            GRANT SELECT ON TABLE public.issue45_review_view TO zeka_adminarea_runtime;
            """);
        try
        {
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("prohibited relation surfaces drifted", drift.Message, StringComparison.Ordinal);

            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await SetTenantAsync(connection, transaction, organisationA);
                await using var protectedTable = new NpgsqlCommand("""
                    SELECT count(*) FROM public."Teams"
                    WHERE "OrganisationId"=@organisation
                    """, connection, transaction);
                protectedTable.Parameters.AddWithValue("organisation", organisationB);
                Assert.Equal(0L, await protectedTable.ExecuteScalarAsync());

                await using var view = new NpgsqlCommand("""
                    SELECT count(*) FROM public.issue45_review_view
                    WHERE "OrganisationId"=@organisation
                    """, connection, transaction);
                view.Parameters.AddWithValue("organisation", organisationB);
                Assert.Equal(1L, await view.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }

            await using var withoutContext = new NpgsqlCommand("""
                SELECT count(*) FROM public.issue45_review_view
                WHERE "OrganisationId"=@organisation
                """, connection);
            withoutContext.Parameters.AddWithValue("organisation", organisationB);
            Assert.Equal(1L, await withoutContext.ExecuteScalarAsync());
        }
        finally
        {
            await database.ExecuteAdministratorAsync("DROP VIEW public.issue45_review_view");
        }

        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Undeclared_materialized_view_and_foreign_table_are_rejected_and_restore_green()
    {
        var organisation = Guid.NewGuid();
        await database.SeedTeamAsAdministratorAsync(organisation, "SurfAce");
        await VerifyCatalogAsync();

        await database.ExecuteAdministratorAsync("""
            CREATE MATERIALIZED VIEW zeka.issue45_review_materialized_view AS
              SELECT "OrganisationId" FROM public."Teams";
            GRANT SELECT ON TABLE zeka.issue45_review_materialized_view TO zeka_adminarea_runtime;
            """);
        try
        {
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("prohibited relation surfaces drifted", drift.Message, StringComparison.Ordinal);
            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM zeka.issue45_review_materialized_view
                WHERE "OrganisationId"=@organisation
                """, connection);
            command.Parameters.AddWithValue("organisation", organisation);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
        }
        finally
        {
            await database.ExecuteAdministratorAsync(
                "DROP MATERIALIZED VIEW zeka.issue45_review_materialized_view");
        }
        await VerifyCatalogAsync();

        await database.ExecuteAdministratorAsync("""
            CREATE EXTENSION file_fdw;
            CREATE SERVER issue45_review_file_server FOREIGN DATA WRAPPER file_fdw;
            CREATE FOREIGN TABLE zeka.issue45_review_foreign_table ("OrganisationId" uuid)
              SERVER issue45_review_file_server OPTIONS (filename '/dev/null', format 'csv');
            GRANT USAGE ON FOREIGN SERVER issue45_review_file_server TO zeka_adminarea_runtime;
            GRANT SELECT ON TABLE zeka.issue45_review_foreign_table TO zeka_adminarea_runtime;
            """);
        try
        {
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("prohibited relation surfaces drifted", drift.Message, StringComparison.Ordinal);
            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM zeka.issue45_review_foreign_table", connection);
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                DROP SERVER IF EXISTS issue45_review_file_server CASCADE;
                DROP EXTENSION IF EXISTS file_fdw;
                """);
        }
        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Undeclared_classic_inheritance_is_rejected_and_restoration_returns_green()
    {
        const string originalName = "chief-private-partner";
        const string modifiedName = "chief-modified-partner";
        var organisationA = Guid.NewGuid();
        await database.ExecuteAdministratorAsync($"""
            INSERT INTO public."Partners"
              ("PartnerNumber", "Name", "Address_City", "Address_Number", "Address_PostalCode",
               "Address_Street", "StaffMemberId", "CategoryOfPartner", "CategoryOfPartnerName",
               "StatusOfPartner", "DateOfAgreementSignature", "IsEconomieSociale", "Note",
               "CreatedBy", "Created", "LastModifiedBy", "LastModified", "Softdelete", "OrganisationId")
            SELECT 999999, '{originalName}', 'Brussels', '1', '1000', 'Review Street',
                   s."Id", 0, 'Review', 0, current_date, false, NULL,
                   'review', now(), 'review', NULL, false, s."OrganisationId"
            FROM public."StaffMembers" s
            ORDER BY s."Id"
            LIMIT 1;
            """);
        await VerifyCatalogAsync();

        await using (var connection = new NpgsqlConnection(database.RuntimeConnectionString))
        {
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, organisationA);
            await using var protectedTable = new NpgsqlCommand(
                "SELECT count(*) FROM public.\"Partners\" WHERE \"Name\"=@name", connection, transaction);
            protectedTable.Parameters.AddWithValue("name", originalName);
            Assert.Equal(0L, await protectedTable.ExecuteScalarAsync());
            await using var parentBeforeDrift = new NpgsqlCommand(
                "SELECT count(*) FROM public.\"TrainingTypes\" WHERE \"Name\"=@name", connection, transaction);
            parentBeforeDrift.Parameters.AddWithValue("name", originalName);
            Assert.Equal(0L, await parentBeforeDrift.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }

        await database.ExecuteAdministratorAsync("""
            ALTER TABLE public."Partners"
              ADD COLUMN "TrainingTypeId" integer NOT NULL DEFAULT 0;
            ALTER TABLE public."Partners" INHERIT public."TrainingTypes";
            """);
        try
        {
            Assert.False(await ExecuteAdministratorScalarAsync<bool>("""
                SELECT child.relispartition
                FROM pg_inherits inheritance
                JOIN pg_class child ON child.oid=inheritance.inhrelid
                WHERE inheritance.inhrelid='public."Partners"'::regclass
                  AND inheritance.inhparent='public."TrainingTypes"'::regclass
                """));

            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await SetTenantAsync(connection, transaction, organisationA);
                await using var protectedTable = new NpgsqlCommand(
                    "SELECT count(*) FROM public.\"Partners\" WHERE \"Name\"=@name", connection, transaction);
                protectedTable.Parameters.AddWithValue("name", originalName);
                Assert.Equal(0L, await protectedTable.ExecuteScalarAsync());

                await using var inheritedRead = new NpgsqlCommand("""
                    SELECT count(*) FROM public."TrainingTypes"
                    WHERE "Name"=@name AND tableoid='public."Partners"'::regclass
                    """, connection, transaction);
                inheritedRead.Parameters.AddWithValue("name", originalName);
                Assert.Equal(1L, await inheritedRead.ExecuteScalarAsync());

                await using var inheritedWrite = new NpgsqlCommand("""
                    UPDATE public."TrainingTypes" SET "Name"=@modified WHERE "Name"=@original
                    """, connection, transaction);
                inheritedWrite.Parameters.AddWithValue("modified", modifiedName);
                inheritedWrite.Parameters.AddWithValue("original", originalName);
                Assert.Equal(1, await inheritedWrite.ExecuteNonQueryAsync());
                await transaction.RollbackAsync();
            }

            await using var withoutContext = new NpgsqlCommand("""
                SELECT count(*) FROM public."TrainingTypes"
                WHERE "Name"=@name AND tableoid='public."Partners"'::regclass
                """, connection);
            withoutContext.Parameters.AddWithValue("name", originalName);
            Assert.Equal(1L, await withoutContext.ExecuteScalarAsync());

            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("relation topology drifted", drift.Message, StringComparison.Ordinal);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                ALTER TABLE public."Partners" NO INHERIT public."TrainingTypes";
                ALTER TABLE public."Partners" DROP COLUMN "TrainingTypeId";
                DELETE FROM public."Partners" WHERE "Name" IN ('chief-private-partner','chief-modified-partner');
                """);
        }

        await VerifyCatalogAsync();
    }

    [Fact]
    public async Task Undeclared_declarative_partition_is_rejected_and_restoration_returns_green()
    {
        await VerifyCatalogAsync();
        await database.ExecuteAdministratorAsync("""
            CREATE TABLE zeka.issue45_partition_parent (id integer NOT NULL)
              PARTITION BY RANGE (id);
            CREATE TABLE zeka.issue45_partition_child
              PARTITION OF zeka.issue45_partition_parent FOR VALUES FROM (0) TO (100);
            """);
        try
        {
            Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
                SELECT child.relispartition
                FROM pg_inherits inheritance
                JOIN pg_class child ON child.oid=inheritance.inhrelid
                WHERE inheritance.inhrelid='zeka.issue45_partition_child'::regclass
                  AND inheritance.inhparent='zeka.issue45_partition_parent'::regclass
                """));
            var drift = await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.Contains("relation topology drifted", drift.Message, StringComparison.Ordinal);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("DROP TABLE zeka.issue45_partition_parent CASCADE");
        }

        await VerifyCatalogAsync();
    }

    [Theory]
    [Trait("Evidence", "ApplicationConformance")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")]
    public async Task Missing_empty_malformed_zero_and_noncanonical_context_fail_closed(string? value)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        if (value is not null)
        {
            await using var set = new NpgsqlCommand(
                "SELECT pg_catalog.set_config('zeka.organisation_id', @value, true)", connection, transaction);
            set.Parameters.AddWithValue("value", value);
            await set.ExecuteScalarAsync();
        }
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT zeka.current_organisation_id()", connection, transaction);
            await command.ExecuteScalarAsync();
        });
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal("Tenant database context is invalid.", exception.MessageText);
    }

    [Fact]
    [Trait("Evidence", "ApplicationConformance")]
    public async Task Raw_sql_and_filter_bypass_cannot_cross_tenant_select_insert_update_or_delete()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedTeamAsync(a, "A");
        await SeedTeamAsync(b, "B");

        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, a);
        Assert.Equal([a], await ReadOrganisationsAsync(connection, transaction));

        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction,
            "INSERT INTO \"Teams\" (\"Name\", \"Acronym\", \"CreatedBy\", \"Created\", \"LastModifiedBy\", \"Softdelete\", \"OrganisationId\") VALUES ('x','x','test',now(),'test',false,@organisation)",
            new NpgsqlParameter("organisation", b)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);
        await transaction.RollbackAsync();

        await using var updateTransaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, updateTransaction, a);
        Assert.Equal(0, await ExecuteCountAsync(connection, updateTransaction,
            "UPDATE \"Teams\" SET \"Name\"='forged' WHERE \"OrganisationId\"=@organisation", b));
        Assert.Equal(0, await ExecuteCountAsync(connection, updateTransaction,
            "DELETE FROM \"Teams\" WHERE \"OrganisationId\"=@organisation", b));
        await updateTransaction.CommitAsync();
    }

    [Fact]
    public async Task Pool_size_one_reuses_session_without_context_after_commit_and_rollback_then_allows_b()
    {
        await using var source = NpgsqlDataSource.Create(database.SingleConnectionRuntimeString);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        int pid;
        await using (var connection = await source.OpenConnectionAsync())
        {
            pid = await BackendPidAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, a);
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(pid, await BackendPidAsync(connection));
            await AssertInvalidContextAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, a);
            await transaction.RollbackAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        {
            Assert.Equal(pid, await BackendPidAsync(connection));
            await AssertInvalidContextAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, b);
            await using var command = new NpgsqlCommand("SELECT zeka.current_organisation_id()", connection, transaction);
            Assert.Equal(b, await command.ExecuteScalarAsync());
            await transaction.CommitAsync();
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Bounded_pool_under_four_organisation_saturation_never_observes_or_writes_foreign_rows(
        int maximumPoolSize)
    {
        var organisations = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var organisation in organisations) await SeedTeamAsync(organisation, organisation.ToString("N")[..6]);
        var connectionString = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
        { MaxPoolSize = maximumPoolSize }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        var correlations = new ConcurrentBag<(string Attempt, int Pid, long Transaction)>();

        await Task.WhenAll(organisations.Select(async (organisation, index) =>
        {
            var foreign = organisations[(index + 1) % organisations.Length];
            for (var iteration = 0; iteration < 12; iteration++)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await SetTenantAsync(connection, transaction, organisation);
                await using (var correlation = new NpgsqlCommand(
                    "SELECT pg_backend_pid(), pg_catalog.txid_current()", connection, transaction))
                await using (var reader = await correlation.ExecuteReaderAsync())
                {
                    Assert.True(await reader.ReadAsync());
                    correlations.Add(($"attempt-{index}-{iteration}", reader.GetInt32(0), reader.GetInt64(1)));
                }
                Assert.Equal([organisation], await ReadOrganisationsAsync(connection, transaction));
                Assert.Equal(0, await ExecuteCountAsync(connection, transaction,
                    "UPDATE \"Teams\" SET \"Name\"=\"Name\" WHERE \"OrganisationId\"=@organisation", foreign));
                await transaction.CommitAsync();
            }
        }));
        Assert.Equal(organisations.Length * 12, correlations.Count);
        Assert.InRange(correlations.Select(value => value.Pid).Distinct().Count(), 1, maximumPoolSize);
        Assert.Equal(correlations.Count, correlations.Select(value => value.Attempt).Distinct().Count());
        Assert.All(correlations, value => Assert.True(value.Transaction > 0));
        output.WriteLine("pool-size={0}; attempts={1}; backend-pids={2}; transaction-markers={3}; foreign-observations=0",
            maximumPoolSize, correlations.Count, correlations.Select(value => value.Pid).Distinct().Count(),
            correlations.Select(value => value.Transaction).Distinct().Count());
    }

    [Fact]
    public async Task Saturated_pool_waiters_timeout_and_cancel_then_the_released_session_is_clean()
    {
        async Task ExerciseAsync(string waiterOutcome)
        {
            var connectionString = new NpgsqlConnectionStringBuilder(database.RuntimeConnectionString)
            {
                MaxPoolSize = 1,
                Timeout = 1
            }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            int heldPid;
            await using (var held = await source.OpenConnectionAsync())
            {
                heldPid = held.ProcessID;
                await using (var transaction = await held.BeginTransactionAsync())
                {
                    await SetTenantAsync(held, transaction, a);
                    await transaction.CommitAsync();
                }

                if (waiterOutcome == "cancelled")
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                        await source.OpenConnectionAsync(cancellation.Token));
                }
                else
                {
                    await Assert.ThrowsAsync<NpgsqlException>(async () => await source.OpenConnectionAsync());
                }
            }

            await using var reused = await source.OpenConnectionAsync();
            Assert.Equal(heldPid, reused.ProcessID);
            await AssertInvalidContextAsync(reused);
            await using var bTransaction = await reused.BeginTransactionAsync();
            await SetTenantAsync(reused, bTransaction, b);
            await using var current = new NpgsqlCommand(
                "SELECT zeka.current_organisation_id()", reused, bTransaction);
            Assert.Equal(b, await current.ExecuteScalarAsync());
            await bTransaction.CommitAsync();
            output.WriteLine("waiter={0}; attempt=2; backend-pid={1}; transaction=reinitialized; foreign-observations=0",
                waiterOutcome, reused.ProcessID);
        }

        await ExerciseAsync("cancelled");
        await ExerciseAsync("timed-out");
    }

    [Theory]
    [InlineData("application-exception")]
    [InlineData("cancellation")]
    [InlineData("command-timeout")]
    public async Task Common_executor_failure_paths_prove_session_disposition_no_context_and_b_isolation(
        string failurePath)
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var suffix = failurePath[..7];
        await SeedTeamAsync(a, suffix);
        await SeedTeamAsync(b, suffix);
        await using var provider = CreateRuntimeProvider(database.SingleConnectionRuntimeString);
        await using var aScope = provider.CreateAsyncScope();
        EstablishTenant(aScope, a);
        var aContext = aScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var aExecutor = aScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var aPids = new List<int>();
        using var cancellation = new CancellationTokenSource();

        async Task ExecuteFailingAttemptAsync()
        {
            await aExecutor.ExecuteAsync(async token =>
            {
                Assert.Equal([a], await aContext.Teams
                    .Select(team => team.OrganisationId).Distinct().ToArrayAsync(token));
                var connection = Assert.IsType<NpgsqlConnection>(aContext.Database.GetDbConnection());
                aPids.Add(connection.ProcessID);
                if (failurePath == "application-exception") throw new SyntheticApplicationException();
                if (failurePath == "cancellation") cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
                await using var command = new NpgsqlCommand(
                    failurePath == "command-timeout" ? "SELECT pg_sleep(5)" : "SELECT pg_sleep(10)",
                    connection,
                    Assert.IsType<NpgsqlTransaction>(aContext.Database.CurrentTransaction!.GetDbTransaction()))
                { CommandTimeout = failurePath == "command-timeout" ? 1 : 30 };
                await command.ExecuteNonQueryAsync(token);
                return true;
            }, failurePath == "cancellation" ? cancellation.Token : CancellationToken.None);
        }

        if (failurePath == "application-exception")
            await Assert.ThrowsAsync<SyntheticApplicationException>(ExecuteFailingAttemptAsync);
        else if (failurePath == "cancellation")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(ExecuteFailingAttemptAsync);
        else
        {
            var retryLimit = await Assert.ThrowsAsync<RetryLimitExceededException>(ExecuteFailingAttemptAsync);
            var timeout = Assert.IsType<NpgsqlException>(retryLimit.InnerException);
            Assert.True(timeout.IsTransient || timeout.InnerException is TimeoutException);
        }
        Assert.NotEmpty(aPids);
        Assert.All(aPids, pid => Assert.True(pid > 0));
        var aPid = aPids[^1];

        var nextConnection = Assert.IsType<NpgsqlConnection>(aContext.Database.GetDbConnection());
        await nextConnection.OpenAsync();
        var nextPid = nextConnection.ProcessID;
        Assert.True(nextPid > 0);
        if (failurePath == "application-exception") Assert.Equal(aPid, nextPid);
        output.WriteLine("{0}: Organisation A attempt PIDs [{1}]; next PID {2}; disposition {3}.",
            failurePath, string.Join(", ", aPids), nextPid,
            aPid == nextPid ? "same-session reuse" : "physical-session replacement");
        await AssertInvalidContextAsync(nextConnection);
        await nextConnection.CloseAsync();

        await using var bScope = provider.CreateAsyncScope();
        EstablishTenant(bScope, b);
        var bContext = bScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var bExecutor = bScope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var observed = await bExecutor.ExecuteAsync(async token =>
        {
            var organisations = await bContext.Teams
                .Select(team => team.OrganisationId).Distinct().ToArrayAsync(token);
            Assert.Equal(nextPid, Assert.IsType<NpgsqlConnection>(bContext.Database.GetDbConnection()).ProcessID);
            return organisations;
        }, CancellationToken.None);
        Assert.Equal([b], observed);

        var mismatch = await Assert.ThrowsAsync<PostgresException>(() => bExecutor.ExecuteAsync(async token =>
        {
            var connection = Assert.IsType<NpgsqlConnection>(bContext.Database.GetDbConnection());
            await using var command = new NpgsqlCommand("""
                INSERT INTO "Teams" ("Name", "Acronym", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "OrganisationId")
                VALUES ('mismatch', 'MIS', 'test', now(), 'test', false, @organisation)
                """, connection,
                Assert.IsType<NpgsqlTransaction>(bContext.Database.CurrentTransaction!.GetDbTransaction()));
            command.Parameters.AddWithValue("organisation", a);
            await command.ExecuteNonQueryAsync(token);
            return true;
        }, CancellationToken.None));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, mismatch.SqlState);
    }

    [Fact]
    public async Task Common_executor_initializes_before_sql_and_guard_rejects_direct_runtime_commands()
    {
        var organisation = Guid.NewGuid();
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>();
        initializer.Establish(new TenantContext(new TenantId(organisation), "test-subject"));
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Database.ExecuteSqlRawAsync("SELECT 1"));
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var result = await executor.ExecuteAsync(async token =>
        {
            context.Add(new Team("Executor", Guid.NewGuid().ToString("N")[..6]));
            await context.SaveChangesAsync(token);
            return await context.Teams.CountAsync(token);
        }, default);
        Assert.True(result >= 1);
    }

    [Fact]
    public async Task Execution_strategy_retry_uses_a_new_transaction_attempt_and_reinitializes_before_sql()
    {
        var organisation = Guid.NewGuid();
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.SingleConnectionRuntimeString
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "test-subject"));
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
        var attempts = new List<(int Pid, Guid Transaction)>();
        var invocation = 0;

        var count = await executor.ExecuteAsync(async token =>
        {
            invocation++;
            var connection = Assert.IsType<NpgsqlConnection>(context.Database.GetDbConnection());
            attempts.Add((connection.ProcessID, context.Database.CurrentTransaction!.TransactionId));
            context.Add(new Team($"Retry {invocation}", Guid.NewGuid().ToString("N")[..6]));
            await context.SaveChangesAsync(token);
            if (invocation == 1)
                throw new NpgsqlException("Synthetic transient failure.", new TimeoutException());
            return await context.Teams.CountAsync(token);
        }, default);

        Assert.True(count >= 1);
        Assert.Equal(2, invocation);
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0].Transaction, attempts[1].Transaction);
        Assert.All(attempts, attempt => Assert.True(attempt.Pid > 0));
    }

    [Fact]
    public async Task Catalog_verifier_fails_closed_for_each_managed_drift_category()
    {
        var policy = "rls_teams_organisation";
        var contextFunction = """
            CREATE OR REPLACE FUNCTION zeka.current_organisation_id() RETURNS uuid
            LANGUAGE plpgsql SECURITY INVOKER
            SET search_path = pg_catalog
            AS $function$
            DECLARE raw text; parsed uuid;
            BEGIN
              raw := current_setting('zeka.organisation_id', true);
              IF raw IS NULL OR raw = '' THEN RAISE EXCEPTION 'Tenant database context is invalid.'; END IF;
              BEGIN parsed := raw::uuid;
              EXCEPTION WHEN invalid_text_representation THEN RAISE EXCEPTION 'Tenant database context is invalid.';
              END;
              IF raw <> lower(parsed::text) OR parsed = '00000000-0000-0000-0000-000000000000'::uuid THEN
                RAISE EXCEPTION 'Tenant database context is invalid.';
              END IF;
              RETURN parsed;
            END $function$;
            """;
        using var commandBuilder = new NpgsqlCommandBuilder();
        var databaseIdentifier = commandBuilder.QuoteIdentifier(database.DatabaseName);
        var expectedPolicy = $"""
            CREATE POLICY {policy} ON "Teams" FOR ALL TO zeka_adminarea_runtime
              USING ("OrganisationId" = zeka.current_organisation_id())
              WITH CHECK ("OrganisationId" = zeka.current_organisation_id())
            """;
        var cases = new (string Mutation, string Restore)[]
        {
            ($"GRANT CONNECT ON DATABASE {databaseIdentifier} TO zeka_adminarea_runtime WITH GRANT OPTION",
                $"REVOKE GRANT OPTION FOR CONNECT ON DATABASE {databaseIdentifier} FROM zeka_adminarea_runtime"),
            ("GRANT USAGE ON SCHEMA public TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE GRANT OPTION FOR USAGE ON SCHEMA public FROM zeka_adminarea_runtime"),
            ("GRANT TRUNCATE ON TABLE \"Teams\" TO zeka_adminarea_runtime",
                "REVOKE TRUNCATE ON TABLE \"Teams\" FROM zeka_adminarea_runtime"),
            ("GRANT SELECT ON TABLE \"Teams\" TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE GRANT OPTION FOR SELECT ON TABLE \"Teams\" FROM zeka_adminarea_runtime"),
            ("GRANT SELECT (\"OrganisationId\") ON TABLE \"Teams\" TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE SELECT (\"OrganisationId\") ON TABLE \"Teams\" FROM zeka_adminarea_runtime"),
            ("CREATE ROLE zeka_issue45_column_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT SELECT (\"Name\") ON TABLE \"Cities\" TO zeka_issue45_column_unexpected",
                "REVOKE SELECT (\"Name\") ON TABLE \"Cities\" FROM zeka_issue45_column_unexpected; DROP ROLE zeka_issue45_column_unexpected"),
            ("GRANT SELECT (\"MigrationId\") ON TABLE \"__EFMigrationsHistory\" TO zeka_adminarea_runtime",
                "REVOKE SELECT (\"MigrationId\") ON TABLE \"__EFMigrationsHistory\" FROM zeka_adminarea_runtime"),
            ("GRANT USAGE ON SEQUENCE \"Teams_Id_seq\" TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE GRANT OPTION FOR USAGE ON SEQUENCE \"Teams_Id_seq\" FROM zeka_adminarea_runtime"),
            ("GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE GRANT OPTION FOR EXECUTE ON FUNCTION zeka.current_organisation_id() FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner GRANT SELECT ON TABLES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE SELECT ON TABLES FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public GRANT SELECT ON TABLES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE SELECT ON TABLES FROM zeka_adminarea_runtime"),
            ($"CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT CONNECT ON DATABASE {databaseIdentifier} TO zeka_issue45_unexpected",
                $"REVOKE CONNECT ON DATABASE {databaseIdentifier} FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT USAGE ON SCHEMA public TO zeka_issue45_unexpected",
                "REVOKE USAGE ON SCHEMA public FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT SELECT ON TABLE \"Teams\" TO zeka_issue45_unexpected",
                "REVOKE SELECT ON TABLE \"Teams\" FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT USAGE ON SEQUENCE \"Teams_Id_seq\" TO zeka_issue45_unexpected",
                "REVOKE USAGE ON SEQUENCE \"Teams_Id_seq\" FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO zeka_issue45_unexpected",
                "REVOKE EXECUTE ON FUNCTION zeka.current_organisation_id() FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("CREATE ROLE zeka_issue45_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public GRANT SELECT ON TABLES TO zeka_issue45_unexpected",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE SELECT ON TABLES FROM zeka_issue45_unexpected; DROP ROLE zeka_issue45_unexpected"),
            ("REVOKE SELECT ON TABLE \"Teams\" FROM zeka_adminarea_runtime",
                "GRANT SELECT ON TABLE \"Teams\" TO zeka_adminarea_runtime"),
            ("GRANT zeka_adminarea_owner TO zeka_adminarea_runtime WITH INHERIT FALSE, SET TRUE, ADMIN FALSE",
                "REVOKE zeka_adminarea_owner FROM zeka_adminarea_runtime"),
            ("ALTER TABLE \"Teams\" OWNER TO postgres",
                "ALTER TABLE \"Teams\" OWNER TO zeka_adminarea_owner"),
            ("ALTER TABLE \"Teams\" DISABLE ROW LEVEL SECURITY",
                "ALTER TABLE \"Teams\" ENABLE ROW LEVEL SECURITY"),
            ("ALTER TABLE \"Teams\" NO FORCE ROW LEVEL SECURITY",
                "ALTER TABLE \"Teams\" FORCE ROW LEVEL SECURITY"),
            ($"DROP POLICY {policy} ON \"Teams\"", expectedPolicy),
            ($"CREATE POLICY rls_teams_unreviewed ON \"Teams\" FOR SELECT TO zeka_adminarea_runtime USING (true)",
                "DROP POLICY rls_teams_unreviewed ON \"Teams\""),
            ($"DROP POLICY {policy} ON \"Teams\"; CREATE POLICY {policy} ON \"Teams\" FOR ALL TO zeka_adminarea_runtime USING (true) WITH CHECK (true)",
                $"DROP POLICY {policy} ON \"Teams\"; {expectedPolicy}"),
            ("ALTER FUNCTION zeka.current_organisation_id() SECURITY DEFINER",
                "ALTER FUNCTION zeka.current_organisation_id() SECURITY INVOKER"),
            ("""
                CREATE OR REPLACE FUNCTION zeka.current_organisation_id() RETURNS uuid
                LANGUAGE plpgsql SECURITY INVOKER
                SET search_path = pg_catalog
                AS $function$
                BEGIN
                  RETURN '11111111-1111-1111-1111-111111111111'::uuid;
                END $function$;
                """, contextFunction),
            ("GRANT EXECUTE ON FUNCTION zeka.current_organisation_id() TO PUBLIC",
                "REVOKE EXECUTE ON FUNCTION zeka.current_organisation_id() FROM PUBLIC")
        };

        foreach (var (mutation, restore) in cases)
        {
            await database.ExecuteAdministratorAsync(mutation);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            }
            finally
            {
                await database.ExecuteAdministratorAsync(restore);
            }
            await VerifyCatalogAsync();
        }
    }

    [Fact]
    public async Task Managed_schema_and_default_privilege_drift_fails_closed_and_full_restoration_returns_green()
    {
        var manifest = RlsSecurityManifestVerifier.Load(typeof(ApplicationDbContext).Assembly);
        Assert.Equal(["public", "zeka"], manifest.ManagedSchemas);
        Assert.Equal(13, manifest.DefaultPrivileges.Length);

        await VerifyCatalogAsync();
        await AssertOwnerCreatedZekaProbesHaveNoNonOwnerPrivilegesAsync();

        await database.ExecuteAdministratorAsync(
            "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka GRANT EXECUTE ON FUNCTIONS TO PUBLIC");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            await database.ExecuteAdministratorAsync("""
                SET ROLE zeka_adminarea_owner;
                CREATE FUNCTION zeka.issue45_default_function_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';
                RESET ROLE;
                """);
            Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
                SELECT EXISTS (
                  SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                  CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) x
                  WHERE n.nspname='zeka' AND p.proname='issue45_default_function_probe'
                    AND x.grantee=0 AND x.privilege_type='EXECUTE')
                """));
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka
                  REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
                DROP FUNCTION IF EXISTS zeka.issue45_default_function_probe();
                """);
        }
        await VerifyCatalogAsync();

        var cases = new (string Mutation, string Restore)[]
        {
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner GRANT USAGE ON TYPES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE USAGE ON TYPES FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner GRANT USAGE ON SCHEMAS TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner REVOKE USAGE ON SCHEMAS FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka GRANT SELECT ON TABLES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE SELECT ON TABLES FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka GRANT USAGE ON SEQUENCES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE USAGE ON SEQUENCES FROM zeka_adminarea_runtime"),
            ("ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka GRANT USAGE ON TYPES TO zeka_adminarea_runtime WITH GRANT OPTION",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE USAGE ON TYPES FROM zeka_adminarea_runtime"),
            ("CREATE ROLE zeka_issue45_default_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT; ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka GRANT USAGE ON TYPES TO zeka_issue45_default_unexpected",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka REVOKE USAGE ON TYPES FROM zeka_issue45_default_unexpected; DROP ROLE zeka_issue45_default_unexpected"),
            ("CREATE SCHEMA zeka_issue45_unreviewed AUTHORIZATION zeka_adminarea_owner",
                "DROP SCHEMA zeka_issue45_unreviewed"),
            ("ALTER SCHEMA zeka OWNER TO postgres",
                "ALTER SCHEMA zeka OWNER TO zeka_adminarea_owner"),
            ("CREATE SCHEMA zeka_issue45_acl_unreviewed AUTHORIZATION postgres; ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka_issue45_acl_unreviewed GRANT SELECT ON TABLES TO zeka_adminarea_runtime",
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA zeka_issue45_acl_unreviewed REVOKE SELECT ON TABLES FROM zeka_adminarea_runtime; DROP SCHEMA zeka_issue45_acl_unreviewed"),
            ("ALTER SCHEMA zeka RENAME TO zeka_issue45_missing",
                "ALTER SCHEMA zeka_issue45_missing RENAME TO zeka")
        };

        foreach (var (mutation, restore) in cases)
        {
            await database.ExecuteAdministratorAsync(mutation);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            }
            finally
            {
                await database.ExecuteAdministratorAsync(restore);
            }
            await VerifyCatalogAsync();
        }
    }

    [Fact]
    public async Task Managed_schema_object_inventory_rls_policy_and_acl_drift_fails_closed_and_restores_green()
    {
        var manifest = RlsSecurityManifestVerifier.Load(typeof(ApplicationDbContext).Assembly);
        Assert.All(manifest.ProtectedTables.Concat(manifest.ExcludedTables),
            table => Assert.Equal("public", table.Schema));
        Assert.Equal(new ManagedObjectIdentity("public", "__EFMigrationsHistory"),
            manifest.MigrationHistoryTable);
        Assert.All(manifest.Sequences, sequence => Assert.Equal("public", sequence.Schema));
        await VerifyCatalogAsync();

        await AssertCatalogMutationFailsAndRestoresAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE zeka.issue45_unclassified_without_tenant (id integer);
            RESET ROLE;
            """, "DROP TABLE zeka.issue45_unclassified_without_tenant");

        await database.ExecuteAdministratorAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE zeka.issue45_unclassified_tenant (
              id integer PRIMARY KEY,
              "OrganisationId" uuid NOT NULL);
            INSERT INTO zeka.issue45_unclassified_tenant VALUES
              (1,'11111111-1111-1111-1111-111111111111'),
              (2,'22222222-2222-2222-2222-222222222222');
            RESET ROLE;
            GRANT SELECT ON TABLE zeka.issue45_unclassified_tenant TO zeka_adminarea_runtime;
            """);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
            Assert.False(await ExecuteAdministratorScalarAsync<bool>("""
                SELECT c.relrowsecurity OR c.relforcerowsecurity
                FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='zeka' AND c.relname='issue45_unclassified_tenant'
                """));
            await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction,
                Guid.Parse("11111111-1111-1111-1111-111111111111"));
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM zeka.issue45_unclassified_tenant
                WHERE "OrganisationId"<>zeka.current_organisation_id()
                """, connection, transaction);
            Assert.Equal(1L, await command.ExecuteScalarAsync());
            await transaction.RollbackAsync();
        }
        finally
        {
            await database.ExecuteAdministratorAsync("DROP TABLE zeka.issue45_unclassified_tenant");
        }
        await VerifyCatalogAsync();

        await AssertCatalogMutationFailsAndRestoresAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE zeka."Teams" (id integer);
            RESET ROLE;
            """, "DROP TABLE zeka.\"Teams\"");

        await database.ExecuteAdministratorAsync("""
            ALTER TABLE public."Teams" SET SCHEMA zeka;
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE public."Teams" (id integer);
            RESET ROLE;
            """);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                DROP TABLE public."Teams";
                ALTER TABLE zeka."Teams" SET SCHEMA public;
                """);
        }
        await VerifyCatalogAsync();

        await AssertCatalogMutationFailsAndRestoresAsync(
            "CREATE POLICY issue45_excluded_policy ON public.\"Cities\" USING (true)",
            "DROP POLICY issue45_excluded_policy ON public.\"Cities\"");
        await AssertCatalogMutationFailsAndRestoresAsync(
            "CREATE POLICY issue45_history_policy ON public.\"__EFMigrationsHistory\" USING (true)",
            "DROP POLICY issue45_history_policy ON public.\"__EFMigrationsHistory\"");

        await AssertCatalogMutationFailsAndRestoresAsync("""
            CREATE ROLE zeka_issue45_object_unexpected NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT;
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE zeka.issue45_acl_probe (id integer, value text);
            RESET ROLE;
            GRANT SELECT ON TABLE zeka.issue45_acl_probe TO zeka_adminarea_runtime WITH GRANT OPTION;
            GRANT UPDATE (value) ON TABLE zeka.issue45_acl_probe TO zeka_issue45_object_unexpected WITH GRANT OPTION;
            """, """
            DROP TABLE zeka.issue45_acl_probe;
            DROP ROLE zeka_issue45_object_unexpected;
            """);

        await AssertCatalogMutationFailsAndRestoresAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE FUNCTION public.issue45_unallowlisted_public() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            RESET ROLE;
            """, "DROP FUNCTION public.issue45_unallowlisted_public()");
        await AssertCatalogMutationFailsAndRestoresAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE FUNCTION zeka.issue45_unallowlisted_zeka() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            RESET ROLE;
            """, "DROP FUNCTION zeka.issue45_unallowlisted_zeka()");

        await AssertCatalogMutationFailsAndRestoresAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE SEQUENCE zeka.issue45_unallowlisted_sequence;
            RESET ROLE;
            GRANT SELECT, USAGE ON SEQUENCE zeka.issue45_unallowlisted_sequence
              TO zeka_adminarea_runtime WITH GRANT OPTION;
            """, "DROP SEQUENCE zeka.issue45_unallowlisted_sequence");
    }

    [Fact]
    public async Task Parameter_privilege_drift_fails_deployment_and_runtime_validation_and_full_revoke_restores_safety()
    {
        await VerifyCatalogAsync();
        await ValidateRuntimeIdentityAsync();

        var cases = new (string Mutation, string Restore, bool ProveReplicaMode)[]
        {
            ("GRANT SET ON PARAMETER session_replication_role TO zeka_adminarea_runtime",
                "REVOKE SET ON PARAMETER session_replication_role FROM zeka_adminarea_runtime", true),
            ("GRANT SET ON PARAMETER session_replication_role TO zeka_adminarea_runtime WITH GRANT OPTION",
                "REVOKE SET ON PARAMETER session_replication_role FROM zeka_adminarea_runtime", false),
            ("GRANT SET ON PARAMETER session_replication_role TO PUBLIC",
                "REVOKE SET ON PARAMETER session_replication_role FROM PUBLIC", false),
            ("GRANT ALTER SYSTEM ON PARAMETER work_mem TO zeka_adminarea_runtime",
                "REVOKE ALTER SYSTEM ON PARAMETER work_mem FROM zeka_adminarea_runtime", false),
            ("GRANT SET ON PARAMETER \"zeka.organisation_id\" TO zeka_adminarea_runtime",
                "REVOKE SET ON PARAMETER \"zeka.organisation_id\" FROM zeka_adminarea_runtime", false)
        };

        foreach (var (mutation, restore, proveReplicaMode) in cases)
        {
            await database.ExecuteAdministratorAsync(mutation);
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
                await Assert.ThrowsAsync<InvalidOperationException>(ValidateRuntimeIdentityAsync);
                if (proveReplicaMode)
                    await ExecuteRuntimeTransactionAsync("SET LOCAL session_replication_role = replica");
            }
            finally
            {
                await database.ExecuteAdministratorAsync(restore);
            }

            await VerifyCatalogAsync();
            await ValidateRuntimeIdentityAsync();
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteRuntimeTransactionAsync("SET LOCAL session_replication_role = replica"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

        var organisation = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenantAsync(connection, transaction, organisation);
        await using var command = new NpgsqlCommand("SELECT zeka.current_organisation_id()", connection, transaction);
        Assert.Equal(organisation, await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
    }

    private async Task VerifyCatalogAsync()
    {
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(database.MigratorConnectionString).Options);
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
    }

    private Task ValidateRuntimeIdentityAsync() =>
        new RuntimeDatabaseIdentityValidator(database.RuntimeConnectionString, "zeka_adminarea_runtime")
            .StartAsync(default);

    private async Task AssertCatalogMutationFailsAndRestoresAsync(string mutation, string restore)
    {
        await database.ExecuteAdministratorAsync(mutation);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(VerifyCatalogAsync);
        }
        finally
        {
            await database.ExecuteAdministratorAsync(restore);
        }
        await VerifyCatalogAsync();
    }

    private async Task AssertOwnerCreatedZekaProbesHaveNoNonOwnerPrivilegesAsync()
    {
        await database.ExecuteAdministratorAsync("""
            SET ROLE zeka_adminarea_owner;
            CREATE TABLE zeka.issue45_relation_probe (id integer);
            CREATE SEQUENCE zeka.issue45_sequence_probe;
            CREATE FUNCTION zeka.issue45_function_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1';
            CREATE TYPE zeka.issue45_type_probe AS ENUM ('value');
            RESET ROLE;
            """);
        try
        {
            var count = await ExecuteAdministratorScalarAsync<long>("""
                WITH object_acls AS (
                  SELECT x.grantee,c.relowner owner
                  FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                  CROSS JOIN LATERAL aclexplode(COALESCE(c.relacl,
                    acldefault(CASE WHEN c.relkind='S' THEN 'S'::"char" ELSE 'r'::"char" END,c.relowner))) x
                  WHERE n.nspname='zeka' AND c.relname IN ('issue45_relation_probe','issue45_sequence_probe')
                  UNION ALL
                  SELECT x.grantee,p.proowner
                  FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
                  CROSS JOIN LATERAL aclexplode(COALESCE(p.proacl,acldefault('f',p.proowner))) x
                  WHERE n.nspname='zeka' AND p.proname='issue45_function_probe'
                  UNION ALL
                  SELECT x.grantee,t.typowner
                  FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace
                  CROSS JOIN LATERAL aclexplode(COALESCE(t.typacl,acldefault('T',t.typowner))) x
                  WHERE n.nspname='zeka' AND t.typname='issue45_type_probe'
                )
                SELECT count(*) FROM object_acls WHERE grantee<>owner
                """);
            Assert.Equal(0, count);
        }
        finally
        {
            await database.ExecuteAdministratorAsync("""
                DROP TABLE IF EXISTS zeka.issue45_relation_probe;
                DROP SEQUENCE IF EXISTS zeka.issue45_sequence_probe;
                DROP FUNCTION IF EXISTS zeka.issue45_function_probe();
                DROP TYPE IF EXISTS zeka.issue45_type_probe;
                """);
        }
    }

    private async Task<T> ExecuteAdministratorScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private Task<string> ReadTeamPolicyExpressionsAsync() => ExecuteAdministratorScalarAsync<string>("""
        SELECT pg_get_expr(p.polqual,p.polrelid,false) || '|' ||
               pg_get_expr(p.polwithcheck,p.polrelid,false)
        FROM pg_policy p
        WHERE p.polrelid='public."Teams"'::regclass
          AND p.polname='rls_teams_organisation'
        """);

    private async Task ExecuteRuntimeTransactionAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    private Task SeedTeamAsync(Guid organisation, string suffix) =>
        database.SeedTeamAsAdministratorAsync(organisation, suffix);

    private static ServiceProvider CreateRuntimeProvider(string connectionString)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = connectionString
        });
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        return services.BuildServiceProvider();
    }

    private static void EstablishTenant(AsyncServiceScope scope, Guid organisation) =>
        scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
            .Establish(new TenantContext(new TenantId(organisation), "test-subject"));

    private static async Task SetTenantAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid organisation)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction);
        command.Parameters.AddWithValue("organisation", organisation.ToString("D"));
        Assert.Equal(organisation.ToString("D"), await command.ExecuteScalarAsync());
    }

    private static async Task<Guid[]> ReadOrganisationsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT DISTINCT \"OrganisationId\" FROM \"Teams\"", connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<Guid>();
        while (await reader.ReadAsync()) values.Add(reader.GetGuid(0));
        return values.ToArray();
    }

    private static async Task<int> ExecuteCountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string sql, Guid organisation)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("organisation", organisation);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> BackendPidAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task AssertInvalidContextAsync(NpgsqlConnection connection)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand("SELECT zeka.current_organisation_id()", connection);
            await command.ExecuteScalarAsync();
        });
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.DoesNotContain("organisation", exception.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SyntheticApplicationException : Exception;
}
