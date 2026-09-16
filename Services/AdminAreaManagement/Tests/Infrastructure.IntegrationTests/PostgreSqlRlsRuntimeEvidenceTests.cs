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
    public async Task Versioned_manifest_matches_EF_classification_and_effective_catalog_bidirectionally()
    {
        var manifest = RlsSecurityManifestVerifier.Load(typeof(ApplicationDbContext).Assembly);
        Assert.Equal(7, manifest.SchemaVersion);
        Assert.Equal(["FOREIGN_TABLE", "MATERIALIZED_VIEW", "VIEW"], manifest.ProhibitedRelationKinds);
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(database.MigratorConnectionString).Options);
        await RlsSecurityManifestVerifier.VerifyAsync(deployment, typeof(ApplicationDbContext).Assembly);
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
                "ALTER DEFAULT PRIVILEGES FOR ROLE zeka_adminarea_owner IN SCHEMA public REVOKE GRANT OPTION FOR SELECT ON TABLES FROM zeka_adminarea_runtime"),
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
