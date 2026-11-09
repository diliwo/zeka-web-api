using ClientManagement.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Application.IntegrationTests;

public sealed class TenantPersistenceMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Empty_initial_database_migrates_without_mapping_and_preserves_tenant_constraints()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250425093851_Initial Migration");

        await migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints");

        (await ScalarAsync<bool>(context,
            """SELECT to_regclass('public."__OrganisationTenantMap"') IS NULL"""))
            .Should().BeTrue();
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'OrganisationId' AND is_nullable = 'NO'
              AND table_name IN ('SocialWorkers', 'SocialCases', 'SchoolRegistrations', 'ProfessionnalExperience',
                'ProfessionalAssessments', 'MonitoringReports', 'Clients', 'Assessments')
            """))
            .Should().Be(8);
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM pg_constraint
            WHERE conname IN (
              'FK_MonitoringReports_Clients_ClientId_OrganisationId',
              'FK_SocialCases_Clients_ClientId_OrganisationId',
              'FK_SocialCases_SocialWorkers_SocialWorkerId_OrganisationId')
            """))
            .Should().Be(3);
        (await ScalarAsync<bool>(context,
            """SELECT indisunique FROM pg_index WHERE indexrelid = 'public."IX_Clients_OrganisationId_ReferenceNumber"'::regclass"""))
            .Should().BeTrue();
        (await ScalarAsync<bool>(context,
            "SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE column_name = 'TenantName' AND table_schema = 'public')"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Missing_mapping_table_with_legacy_rows_blocks_migration_without_schema_changes()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250425093851_Initial Migration");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SocialWorkers"
                ("FirstName", "LastName", "UserName", "TeamName", "TeamAcronym", "CreatedBy", "Created", "LastModifiedBy", "TenantName", "Softdelete")
            VALUES ('Ada', 'Lovelace', 'ada', 'Support', 'SUP', 'test', now(), '', 'Legacy', false)
            """);

        var migration = () => migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints");

        await migration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Create and validate __OrganisationTenantMap"));
        (await ScalarAsync<bool>(context,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'SocialWorkers' AND column_name = 'TenantName')"))
            .Should().BeTrue();
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'OrganisationId'
              AND table_name IN ('SocialWorkers', 'SocialCases', 'SchoolRegistrations', 'ProfessionnalExperience',
                'ProfessionalAssessments', 'MonitoringReports', 'Clients', 'Assessments')
            """))
            .Should().Be(0);
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM "SocialWorkers"
            """))
            .Should().Be(1);
        (await ScalarAsync<string>(context,
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1"""))
            .Should().Be("20250425093851_Initial Migration");
    }

    [Fact]
    public async Task Failed_backfill_can_be_remediated_and_retried_without_data_loss()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250425093851_Initial Migration");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "SocialWorkers"
                ("FirstName", "LastName", "UserName", "TeamName", "TeamAcronym", "CreatedBy", "Created", "LastModifiedBy", "TenantName", "Softdelete")
            VALUES ('Ada', 'Lovelace', 'ada', 'Support', 'SUP', 'test', now(), '', 'Legacy', false)
            """);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__OrganisationTenantMap\" (\"TenantName\" text PRIMARY KEY, \"OrganisationId\" uuid NOT NULL UNIQUE)");

        var incompleteMigration = () => migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints");
        await incompleteMigration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Tenant backfill is incomplete"));
        (await ScalarAsync<long>(context, "SELECT count(*) FROM \"SocialWorkers\""))
            .Should().Be(1);

        var organisationId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO \"__OrganisationTenantMap\" VALUES ('Legacy', {organisationId})");

        await migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints");

        (await ScalarAsync<long>(context, $"SELECT count(*) FROM \"SocialWorkers\" WHERE \"OrganisationId\" = '{organisationId}'"))
            .Should().Be(1);
        var constraints = await ScalarAsync<long>(context, """
            SELECT count(*) FROM pg_constraint
            WHERE conname IN (
              'FK_MonitoringReports_Clients_ClientId_OrganisationId',
              'FK_SocialCases_Clients_ClientId_OrganisationId',
              'FK_SocialCases_SocialWorkers_SocialWorkerId_OrganisationId')
            """);
        constraints.Should().Be(3);
        (await ScalarAsync<bool>(context,
            "SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE column_name = 'TenantName' AND table_schema = 'public' AND table_name <> '__OrganisationTenantMap')"))
            .Should().BeTrue();

        var unsafeRollback = () => migrator.MigrateAsync("20250425093851_Initial Migration");
        await unsafeRollback.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Automatic rollback is disabled"));
    }

    private async Task<DeploymentDbContext> CreateContextAsync()
    {
        var databaseName = $"client_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = databaseName };
        return new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString.ConnectionString).Options);
    }

    private static async Task<T> ScalarAsync<T>(DeploymentDbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

public sealed class SharedPersistenceRegistrationTests
{
    [Fact]
    public void DbContext_uses_only_the_service_shared_connection()
    {
        const string connectionString = "Host=shared-client;Database=client;Username=test;Password=test";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ClientApiConnection"] = connectionString
            })
            .Build();
        var services = new ServiceCollection();

        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);

        services.Count(descriptor => descriptor.ServiceType == typeof(DbContextOptions<ApplicationDbContext>))
            .Should().Be(1);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()
            .Should().Be(connectionString);
    }
}
