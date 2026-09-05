using AdminAreaManagement.Infrastructure.Persistence;
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
    public async Task Known_baseline_collision_blocks_migration_before_schema_changes()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisationA = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {{organisationA}});
            """);

        var migration = () => migrator.MigrateAsync();

        await migration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Duplicate StaffMembers"));
        (await ScalarAsync<bool>(context,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'StaffMembers' AND column_name = 'TenantName')"))
            .Should().BeTrue();
        (await ScalarAsync<long>(context, "SELECT count(*) FROM \"StaffMembers\""))
            .Should().Be(3);
    }

    [Fact]
    public async Task Reviewed_remediation_allows_backfill_and_tenant_constraints()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {{organisationA}});
            """);
        // Reviewed remediation fixture: seed record 1 receives its approved replacement key.
        await context.Database.ExecuteSqlRawAsync("UPDATE \"StaffMembers\" SET \"UserName\" = 'jdoe' WHERE \"Id\" = 1");

        await migrator.MigrateAsync();

        (await ScalarAsync<long>(context, "SELECT count(*) FROM \"StaffMembers\""))
            .Should().Be(3);
        (await ScalarAsync<long>(context, $"SELECT count(*) FROM \"StaffMembers\" WHERE \"OrganisationId\" = '{organisationA}'"))
            .Should().Be(3);

        var crossTenantInsert = async () => await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "StaffMembers"
                ("FirstName", "LastName", "UserName", "TeamId", "OrganisationId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete")
            VALUES ('Cross', 'Tenant', 'cross-tenant', 1, {{organisationB}}, 'test', now(), '', false);
            """);

        await crossTenantInsert.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.ForeignKeyViolation);

        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Teams" ("Name", "Acronym", "OrganisationId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete")
            VALUES ('Other organisation', 'SES', {{organisationB}}, 'test', now(), '', false);
            """);

        var duplicateInSameOrganisation = async () => await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Teams" ("Name", "Acronym", "OrganisationId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete")
            VALUES ('Duplicate', 'SES', {{organisationB}}, 'test', now(), '', false);
            """);
        await duplicateInSameOrganisation.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UniqueViolation);

        var unsafeRollback = () => migrator.MigrateAsync("20250427103057_Initial Migration");
        await unsafeRollback.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Automatic rollback is disabled"));
    }

    [Fact]
    public async Task Missing_mapping_blocks_migration_after_collision_remediation()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        await context.Database.ExecuteSqlRawAsync("UPDATE \"StaffMembers\" SET \"UserName\" = 'jdoe' WHERE \"Id\" = 1");

        var migration = () => migrator.MigrateAsync();

        await migration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Create and validate __OrganisationTenantMap"));
        (await ScalarAsync<long>(context, "SELECT count(*) FROM \"StaffMembers\""))
            .Should().Be(3);
    }

    private async Task<ApplicationDbContext> CreateContextAsync()
    {
        var databaseName = $"admin_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = databaseName };
        return new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connectionString.ConnectionString).Options);
    }

    private static async Task<T> ScalarAsync<T>(ApplicationDbContext context, string sql)
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
        const string connectionString = "Host=shared-admin;Database=admin;Username=test;Password=test";
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = connectionString
        });
        var services = new ServiceCollection();

        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);

        services.Count(descriptor => descriptor.ServiceType == typeof(DbContextOptions<ApplicationDbContext>))
            .Should().Be(1);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.GetConnectionString()
            .Should().Be(connectionString);
    }
}
