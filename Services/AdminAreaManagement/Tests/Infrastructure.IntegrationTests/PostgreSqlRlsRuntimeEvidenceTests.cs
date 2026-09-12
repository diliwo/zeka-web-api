using System.Data;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

public sealed class PostgreSqlRlsRuntimeDatabase : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string RuntimePassword = "test-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

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

    private async Task ApplyAllMigrationsAsMigratorAsync()
    {
        await using var deployment = Deployment(MigratorConnectionString);
        await deployment.Database.OpenConnectionAsync();
        await deployment.Database.ExecuteSqlRawAsync("SET ROLE zeka_adminarea_owner");
        var migrator = deployment.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisation = Guid.NewGuid();
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
        await deployment.GetService<IMigrator>().MigrateAsync();
    }

    private static DeploymentDbContext Deployment(string connectionString) => new(
        new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString).Options);

    public async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, sql);
    }

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

public sealed class PostgreSqlRlsRuntimeEvidenceTests(PostgreSqlRlsRuntimeDatabase database)
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
    public async Task Versioned_manifest_matches_EF_classification_and_effective_catalog_bidirectionally()
    {
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(database.MigratorConnectionString).Options);
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
    }

    [Theory]
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
    public async Task Pool_size_one_reuses_session_without_context_after_commit_rollback_and_exception_then_allows_b()
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
            await SetTenantAsync(connection, transaction, a);
            await transaction.RollbackAsync(); // application exception cleanup
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

        await Task.WhenAll(organisations.Select(async (organisation, index) =>
        {
            var foreign = organisations[(index + 1) % organisations.Length];
            for (var iteration = 0; iteration < 12; iteration++)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await SetTenantAsync(connection, transaction, organisation);
                Assert.Equal([organisation], await ReadOrganisationsAsync(connection, transaction));
                Assert.Equal(0, await ExecuteCountAsync(connection, transaction,
                    "UPDATE \"Teams\" SET \"Name\"=\"Name\" WHERE \"OrganisationId\"=@organisation", foreign));
                await transaction.CommitAsync();
            }
        }));
    }

    [Fact]
    public async Task Cancellation_never_leaves_a_reused_or_replacement_session_with_tenant_context()
    {
        await using var source = NpgsqlDataSource.Create(database.SingleConnectionRuntimeString);
        int firstPid;
        await using (var connection = await source.OpenConnectionAsync())
        {
            firstPid = await BackendPidAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, Guid.NewGuid());
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_sleep(10)", connection, transaction);
                await command.ExecuteNonQueryAsync(cancellation.Token);
            });
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using var next = await source.OpenConnectionAsync();
        Assert.True(await BackendPidAsync(next) > 0); // cancellation may preserve or replace the provider session
        await AssertInvalidContextAsync(next);
    }

    [Fact]
    public async Task Command_timeout_never_leaves_a_reused_or_replacement_session_with_tenant_context()
    {
        await using var source = NpgsqlDataSource.Create(database.SingleConnectionRuntimeString);
        int firstPid;
        await using (var connection = await source.OpenConnectionAsync())
        {
            firstPid = await BackendPidAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync();
            await SetTenantAsync(connection, transaction, Guid.NewGuid());
            var timeout = await Assert.ThrowsAnyAsync<NpgsqlException>(async () =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_sleep(5)", connection, transaction)
                { CommandTimeout = 1 };
                await command.ExecuteNonQueryAsync();
            });
            Assert.True(timeout.IsTransient || timeout.InnerException is TimeoutException);
            await transaction.RollbackAsync(CancellationToken.None);
        }

        await using var next = await source.OpenConnectionAsync();
        Assert.True(firstPid > 0 && await BackendPidAsync(next) > 0);
        await AssertInvalidContextAsync(next);
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
        var expectedPolicy = $"""
            CREATE POLICY {policy} ON "Teams" FOR ALL TO zeka_adminarea_runtime
              USING ("OrganisationId" = zeka.current_organisation_id())
              WITH CHECK ("OrganisationId" = zeka.current_organisation_id())
            """;
        var cases = new (string Mutation, string Restore)[]
        {
            ("GRANT TRUNCATE ON TABLE \"Teams\" TO zeka_adminarea_runtime",
                "REVOKE TRUNCATE ON TABLE \"Teams\" FROM zeka_adminarea_runtime"),
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

    private async Task VerifyCatalogAsync()
    {
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(database.MigratorConnectionString).Options);
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
    }

    private Task SeedTeamAsync(Guid organisation, string suffix) =>
        database.SeedTeamAsAdministratorAsync(organisation, suffix);

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
}
