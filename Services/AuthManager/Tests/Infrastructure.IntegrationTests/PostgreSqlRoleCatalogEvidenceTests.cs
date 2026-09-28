using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class PostgreSqlAuthRoleCatalogEvidenceTests : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string RuntimePassword = "test-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Unexpected_managed_schema_owner_fails_closed_without_adoption()
    {
        await ExecuteAdministratorAsync("CREATE SCHEMA zeka");
        var originalIdentity = await ExecuteAdministratorScalarAsync<string>("""
            SELECT namespace.oid::text || '|' || pg_catalog.pg_get_userbyid(namespace.nspowner)
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname='zeka'
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql")));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal("managed schema zeka has unexpected owner", exception.MessageText);
        Assert.Equal(originalIdentity, await ExecuteAdministratorScalarAsync<string>("""
            SELECT namespace.oid::text || '|' || pg_catalog.pg_get_userbyid(namespace.nspowner)
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname='zeka'
            """));
    }

    [Fact]
    public async Task Fresh_database_migrates_through_owner_assumption_and_matches_auth_manifest()
    {
        Assert.False(await ExecuteAdministratorScalarAsync<bool>(
            "SELECT pg_catalog.to_regnamespace('zeka') IS NOT NULL"));

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        var provisionedSchemaOid = await ExecuteAdministratorScalarAsync<long>(
            "SELECT oid::bigint FROM pg_catalog.pg_namespace WHERE nspname='zeka'");
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactManagedSchemaPrivilegesSql));

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        Assert.Equal(provisionedSchemaOid, await ExecuteAdministratorScalarAsync<long>(
            "SELECT oid::bigint FROM pg_catalog.pg_namespace WHERE nspname='zeka'"));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactManagedSchemaPrivilegesSql));

        await ExecuteAdministratorAsync($"ALTER ROLE zeka_auth_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}';");

        var migratorConnection = Connection("zeka_auth_migrator", MigratorPassword);
        var runtimeConnection = Connection("zeka_auth_runtime", RuntimePassword);
        await AssertInsufficientPrivilegeAsync(migratorConnection, "CREATE SCHEMA issue46_migrator_forbidden");
        await AssertInsufficientPrivilegeAsync(runtimeConnection, "CREATE SCHEMA issue46_runtime_forbidden");
        await AssertInsufficientPrivilegeAsync(migratorConnection,
            "SET ROLE zeka_auth_owner; CREATE SCHEMA issue46_owner_forbidden");
        await AssertInsufficientPrivilegeAsync(migratorConnection,
            "CREATE TABLE zeka.issue46_direct_migrator_forbidden(id integer)");
        await ExecuteAsync(migratorConnection, """
            SET ROLE zeka_auth_owner;
            CREATE TABLE zeka.issue46_owner_assumption_probe(id integer);
            DROP TABLE zeka.issue46_owner_assumption_probe;
            RESET ROLE;
            """);

        await using (var migration = Context(migratorConnection))
        {
            await migration.Database.OpenConnectionAsync();
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            var migrations = migration.Database.GetMigrations().ToArray();
            Assert.True(migrations.Length > 1);
            await migration.GetService<IMigrator>().MigrateAsync(migrations[^2]);
            Assert.Equal([migrations[^1]], await migration.Database.GetPendingMigrationsAsync());
            await migration.GetService<IMigrator>().MigrateAsync();
            Assert.Empty(await migration.Database.GetPendingMigrationsAsync());
        }

        await using var verification = Context(migratorConnection);
        // Reconcile the actual migrated lifecycle schema before runtime/catalog verification.
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        await new RuntimeDatabaseIdentityValidator(runtimeConnection, "zeka_auth_runtime").StartAsync(default);

        var maskedAdministrator = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Options = "-c role=zeka_auth_runtime",
            Pooling = false
        }.ConnectionString;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeDatabaseIdentityValidator(maskedAdministrator, "zeka_auth_runtime").StartAsync(default));

        await ExecuteAdministratorAsync($"""
            SET ROLE zeka_auth_owner;
            CREATE TABLE public.issue45_unknown_auth(id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
            RESET ROLE;
            GRANT ALL ON TABLE public.issue45_unknown_auth TO zeka_auth_runtime;
            GRANT ALL ON SEQUENCE public.issue45_unknown_auth_id_seq TO zeka_auth_runtime;
            GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO zeka_auth_runtime;
            ALTER ROLE zeka_auth_owner IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_migrator IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_runtime IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_closure_recovery IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_closure_recovery LOGIN BYPASSRLS CREATEDB CREATEROLE INHERIT REPLICATION;
            GRANT CREATE, TEMPORARY ON DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} TO zeka_auth_closure_recovery;
            GRANT CREATE, USAGE ON SCHEMA public, zeka TO zeka_auth_closure_recovery;
            GRANT zeka_auth_closure_recovery TO zeka_auth_runtime WITH ADMIN OPTION;
            GRANT ALL ON TABLE public."AspNetUsers" TO zeka_auth_closure_recovery;
            GRANT ALL ON SEQUENCE public."AspNetUserClaims_Id_seq" TO zeka_auth_closure_recovery;
            """);
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
            SELECT
              (SELECT count(*)=0 FROM pg_catalog.pg_db_role_setting s
               JOIN pg_catalog.pg_roles r ON r.oid=s.setrole
               JOIN pg_catalog.pg_database d ON d.oid=s.setdatabase
               WHERE d.datname=pg_catalog.current_database()
                 AND r.rolname=ANY(ARRAY[
                   'zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime',
                   'zeka_auth_closure_recovery']))
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','SELECT')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','INSERT')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','UPDATE')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','DELETE')
              AND NOT pg_catalog.has_table_privilege('zeka_auth_runtime','public."__EFMigrationsHistory"','SELECT')
              AND NOT pg_catalog.has_table_privilege('zeka_auth_runtime','public.issue45_unknown_auth','SELECT')
              AND NOT pg_catalog.has_sequence_privilege('zeka_auth_runtime','public.issue45_unknown_auth_id_seq','USAGE')
              AND NOT pg_catalog.has_table_privilege(
                'zeka_auth_closure_recovery','public."AspNetUsers"','SELECT')
              AND NOT pg_catalog.has_sequence_privilege(
                'zeka_auth_closure_recovery','public."AspNetUserClaims_Id_seq"','USAGE')
              AND NOT pg_catalog.pg_has_role(
                'zeka_auth_runtime','zeka_auth_closure_recovery','MEMBER')
            """));
        await ExecuteAdministratorAsync("DROP TABLE public.issue45_unknown_auth");
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        await AssertInsufficientPrivilegeAsync(runtimeConnection,
            "GRANT zeka_auth_closure_recovery TO zeka_auth_runtime");

        await ExecuteAdministratorAsync("""
            CREATE FUNCTION zeka.issue46_unknown_function() RETURNS integer
              LANGUAGE sql SECURITY INVOKER AS 'SELECT 1';
            CREATE FUNCTION public.issue46_unknown_function() RETURNS integer
              LANGUAGE sql SECURITY INVOKER AS 'SELECT 1';
            CREATE TABLE public.issue46_unknown_lifecycle (id uuid PRIMARY KEY);
            CREATE TABLE zeka.issue46_unknown_lifecycle (id uuid PRIMARY KEY);
            GRANT CREATE, USAGE ON SCHEMA zeka TO zeka_auth_runtime;
            GRANT ALL ON ALL TABLES IN SCHEMA public, zeka TO zeka_auth_runtime;
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public, zeka TO zeka_auth_runtime;
            GRANT SELECT ON TABLE public."AuthClosureParticipantInbox" TO zeka_auth_migrator;
            GRANT UPDATE ("PayloadSha256") ON TABLE public."AuthClosureParticipantInbox" TO zeka_auth_migrator;
            GRANT EXECUTE ON FUNCTION zeka.reject_auth_owned_write_during_closure_fence()
              TO zeka_auth_migrator;
            """);

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));

        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactLifecycleTablePrivilegesSql));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactLifecycleColumnPrivilegesSql));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
            SELECT
              pg_catalog.has_schema_privilege('zeka_auth_runtime', 'public', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'public', 'CREATE')
              AND pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'CREATE')
              AND (
                SELECT pg_catalog.array_agg(n.nspname || '.' || p.proname ORDER BY n.nspname, p.proname)
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(ARRAY['public','zeka'])
                  AND pg_catalog.has_function_privilege('zeka_auth_runtime', p.oid, 'EXECUTE')
              ) = ARRAY['zeka.current_organisation_id',
                        'zeka.reject_auth_membership_write_during_export_fence',
                        'zeka.reject_auth_organisation_write_during_closure_fence',
                        'zeka.reject_auth_owned_write_during_closure_fence']
              AND NOT pg_catalog.has_function_privilege(
                'zeka_auth_runtime',
                'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
              AND pg_catalog.has_function_privilege(
                'zeka_auth_closure_recovery',
                'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
              AND (
                SELECT pg_catalog.array_agg(n.nspname || '.' || p.proname ORDER BY n.nspname, p.proname)
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(ARRAY['public','zeka'])
                  AND pg_catalog.has_function_privilege(
                    'zeka_auth_closure_recovery', p.oid, 'EXECUTE')
              ) = ARRAY['zeka.release_auth_closure_fence']
              AND NOT EXISTS (
                SELECT FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
                LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                WHERE n.nspname='zeka'
                  AND p.proname=ANY(ARRAY[
                    'reject_auth_membership_write_during_export_fence',
                    'reject_auth_organisation_write_during_closure_fence',
                    'reject_auth_owned_write_during_closure_fence',
                    'release_auth_closure_fence'])
                  AND acl.grantee<>p.proowner
                  AND (grantee.rolname IS DISTINCT FROM CASE
                        WHEN p.proname='release_auth_closure_fence'
                          THEN 'zeka_auth_closure_recovery'
                        ELSE 'zeka_auth_runtime'
                      END
                    OR acl.privilege_type<>'EXECUTE'
                    OR acl.is_grantable)
              )
              AND NOT EXISTS (
                SELECT FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='zeka'
                  AND p.proname=ANY(ARRAY[
                    'reject_auth_membership_write_during_export_fence',
                    'reject_auth_organisation_write_during_closure_fence',
                    'reject_auth_owned_write_during_closure_fence',
                    'release_auth_closure_fence'])
                  AND pg_catalog.pg_get_userbyid(p.proowner)<>'zeka_auth_owner'
              )
            """));

        await ExecuteAdministratorAsync("""
            DROP TABLE public.issue46_unknown_lifecycle,
              zeka.issue46_unknown_lifecycle;
            DROP FUNCTION public.issue46_unknown_function();
            DROP FUNCTION zeka.issue46_unknown_function();
            """);
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
    }

    private static AuthDbContext Context(string connectionString) => new(
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).Options);

    private string Connection(string username, string password) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = username, Password = password }.ConnectionString;

    private async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ExecuteAdministratorScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertInsufficientPrivilegeAsync(string connectionString, string sql)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static string ReadBootstrapScript(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed database role bootstrap script was not found.", fileName);
    }

    private const string ExactLifecycleTablePrivilegesSql = """
        WITH expected(table_name, grantee, privilege, is_grantable) AS (
          SELECT table_name, 'zeka_auth_runtime', privilege, false
          FROM unnest(ARRAY[
            'AspNetRoleClaims','AspNetRoles','AspNetUserClaims','AspNetUserLogins','AspNetUserRoles',
            'AspNetUserTokens','AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships',
            'Organisations','OutboxMessages','PermissionSets'
          ]) table_name
          CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE']) privilege
          UNION ALL VALUES
            ('__EFMigrationsHistory','zeka_auth_migrator','SELECT',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','INSERT',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','UPDATE',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','DELETE',false),
            ('LifecycleParticipantRegistryRevisions','zeka_auth_runtime','SELECT',false),
            ('LifecycleParticipantRegistryBindings','zeka_auth_runtime','SELECT',false),
            ('LifecycleParticipantRegistryActivation','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantExecutions','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantExecutions','zeka_auth_runtime','INSERT',false),
            ('AuthExportParticipantInbox','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantInbox','zeka_auth_runtime','INSERT',false),
            ('AuthExportParticipantOutbox','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantOutbox','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantExecutions','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantExecutions','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantInbox','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantInbox','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantOutbox','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantOutbox','zeka_auth_runtime','INSERT',false),
            ('OrganisationLifecycleOperations','zeka_auth_runtime','SELECT',false),
            ('OrganisationLifecycleOperations','zeka_auth_runtime','INSERT',false),
            ('OrganisationLifecycleParticipants','zeka_auth_runtime','SELECT',false),
            ('OrganisationLifecycleParticipants','zeka_auth_runtime','INSERT',false),
            ('MembershipPermissionGrants','zeka_auth_runtime','SELECT',false),
            ('MembershipPermissionGrants','zeka_auth_runtime','INSERT',false),
            ('LifecycleCoordinatorLeases','zeka_auth_runtime','SELECT',false),
            ('LifecycleCoordinatorLeases','zeka_auth_runtime','INSERT',false),
            ('LifecycleClosureFenceReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleClosureFenceReceipts','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportFenceReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportFenceReceipts','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportFragments','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportFragments','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportPackages','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportPackages','zeka_auth_runtime','INSERT',false),
            ('LifecycleInboxReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleInboxReceipts','zeka_auth_runtime','INSERT',false)
        ), actual(table_name, grantee, privilege, is_grantable) AS (
          SELECT c.relname, COALESCE(grantee.rolname, 'PUBLIC'), acl.privilege_type, acl.is_grantable
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE n.nspname=ANY(ARRAY['public','zeka'])
            AND c.relkind IN ('r','p')
            AND acl.grantee<>c.relowner
        )
        SELECT NOT EXISTS (SELECT * FROM actual EXCEPT SELECT * FROM expected)
          AND NOT EXISTS (SELECT * FROM expected EXCEPT SELECT * FROM actual)
          AND NOT EXISTS (
            SELECT FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=ANY(ARRAY['public','zeka'])
              AND c.relkind IN ('r','p')
              AND pg_catalog.pg_get_userbyid(c.relowner)<>'zeka_auth_owner')
        """;

    private const string ExactLifecycleColumnPrivilegesSql = """
        WITH expected(table_name, column_name, grantee, privilege, is_grantable) AS (
          SELECT table_name, column_name, 'zeka_auth_runtime', 'UPDATE', false
          FROM (VALUES
            ('OrganisationLifecycleOperations', ARRAY['State','Revision','SnapshotAt','FenceEvidenceHash',
              'PackageSha256','PackageReference','FailureCode','CompletedAt','IsActive','ClosingAt',
              'ArchivedAt','ClosureFenceEvidenceHash']::text[]),
            ('OrganisationLifecycleParticipants', ARRAY['State','FailureBoundaryDisposition','FailureCode','FailureRetryable','FailedAt']::text[]),
            ('LifecycleCoordinatorLeases', ARRAY['LeaseId','ExpiresAt','Version']::text[]),
            ('AuthExportParticipantExecutions', ARRAY['SnapshotAt','FenceEvidenceHash','FragmentHash',
              'CategoriesJson','ReleasedAt','State']::text[]),
            ('AuthClosureParticipantExecutions', ARRAY[]::text[]),
            ('MembershipPermissionGrants', ARRAY['RevokedByMembershipId','RevokedBySubjectId',
              'RevokedAtUtc','ConcurrencyVersion']::text[])
          ) inventory(table_name, update_columns)
          CROSS JOIN LATERAL pg_catalog.unnest(inventory.update_columns) column_name
        ), actual(table_name, column_name, grantee, privilege, is_grantable) AS (
          SELECT relation.relname, attribute.attname, COALESCE(grantee.rolname, 'PUBLIC'),
            acl.privilege_type, acl.is_grantable
          FROM pg_catalog.pg_attribute attribute
          JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
          JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE namespace.nspname=ANY(ARRAY['public','zeka'])
            AND relation.relkind IN ('r','p')
            AND attribute.attnum>0 AND NOT attribute.attisdropped
            AND acl.grantee<>relation.relowner
        )
        SELECT NOT EXISTS (SELECT * FROM actual EXCEPT SELECT * FROM expected)
          AND NOT EXISTS (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        """;

    private const string ExactManagedSchemaPrivilegesSql = """
        WITH schema_acl AS (
          SELECT COALESCE(grantee.rolname, 'PUBLIC') grantee,
            acl.privilege_type,
            acl.is_grantable
          FROM pg_catalog.pg_namespace namespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(namespace.nspacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE namespace.nspname='zeka'
            AND acl.grantee<>namespace.nspowner
        )
        SELECT
          (SELECT pg_catalog.pg_get_userbyid(namespace.nspowner)='zeka_auth_owner'
             FROM pg_catalog.pg_namespace namespace WHERE namespace.nspname='zeka')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_owner', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_migrator', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_runtime', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege(
            'zeka_auth_closure_recovery', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_migrator', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_migrator', 'zeka', 'CREATE')
          AND pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'CREATE')
          AND pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'zeka', 'CREATE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'public', 'USAGE')
          AND (SELECT pg_catalog.count(*)=2 FROM schema_acl)
          AND EXISTS (
            SELECT FROM schema_acl
            WHERE grantee='zeka_auth_runtime'
              AND privilege_type='USAGE'
              AND NOT is_grantable)
          AND EXISTS (
            SELECT FROM schema_acl
            WHERE grantee='zeka_auth_closure_recovery'
              AND privilege_type='USAGE'
              AND NOT is_grantable)
        """;
}
