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
    private readonly PostgreSqlContainer? _postgres;
    private readonly string? _administratorConnectionString;

    public TenantPersistenceMigrationTests() =>
        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    // Supplies the focused owner-local runner's ephemeral connection only in memory.
    // The normal xUnit/Testcontainers constructor and lifecycle remain unchanged.
    internal TenantPersistenceMigrationTests(string administratorConnectionString) =>
        _administratorConnectionString = administratorConnectionString
            ?? throw new ArgumentNullException(nameof(administratorConnectionString));

    private string AdministratorConnectionString =>
        _administratorConnectionString ?? _postgres!.GetConnectionString();

    public Task InitializeAsync() => _postgres?.StartAsync() ?? Task.CompletedTask;
    public Task DisposeAsync() => _postgres is null ? Task.CompletedTask : _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Fresh_database_has_no_demo_rows_and_preserves_shared_reference_identities()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        // All seed changes are included; the latest-install spike covers role bootstrap/RLS.
        await migrator.MigrateAsync("20260910165920_ExplicitTenantEnforcement");
        (await ScalarAsync<long>(context, """SELECT count(*) FROM "Teams" """)).Should().Be(0);
        (await ScalarAsync<long>(context, """SELECT count(*) FROM "StaffMembers" """)).Should().Be(0);
        (await ScalarAsync<bool>(context,
            """SELECT to_regclass('public."__OrganisationTenantMap"') IS NULL AND to_regclass('public."__StaffMembershipMap"') IS NULL"""))
            .Should().BeTrue();
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'OrganisationId'
              AND table_name IN ('Cities', 'Nationalities', 'Professions', 'Schools',
                'TrainingFields', 'TrainingTypes', 'Trainings')
            """)).Should().Be(0);
        (await ScalarAsync<string>(context, """
            SELECT string_agg("CityId"::text || ':' || "Name" || ':' || "Country", '|' ORDER BY "CityId") FROM "Cities"
            """)).Should().Be("1:Brussels:Belgium|2:Oslo:Norway|3:Cape Town:South Africa");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("NationalityId"::text || ':' || "Name", '|' ORDER BY "NationalityId") FROM "Nationalities"
            """)).Should().Be("1:Belgian|2:Norway|3:South African");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("Id"::text || ':' || "Name", '|' ORDER BY "Id") FROM "Professions"
            """)).Should().Be("1:Kitchen, assistant|2:Baker|3:Computer repair technicien");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("Id"::text || ':' || "Name" || ':' || "Locality", '|' ORDER BY "Id") FROM "Schools"
            """)).Should().Be("1:Massachusetts Institute Of Technology:Cambridge|2:Stanford University:Stanford|3:International University of Applied Science:Berlin");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("TrainingFieldId"::text || ':' || "Name", '|' ORDER BY "TrainingFieldId") FROM "TrainingFields"
            """)).Should().Be("1:Political Sociology|2:Information Technology|3:Languages");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("TrainingTypeId"::text || ':' || "Name", '|' ORDER BY "TrainingTypeId") FROM "TrainingTypes"
            """)).Should().Be("1:Bachelor|2:Master|3:Master of Business Administration");
        (await ScalarAsync<string>(context, """
            SELECT string_agg("Id"::text || ':' || "Name" || ':' || "TrainingFieldId"::text, '|' ORDER BY "Id") FROM "Trainings"
            """)).Should().Be("1:Software Developement:2|2:English:3|3:Deutch:3");
    }

    [Fact]
    public async Task Explicit_duplicate_legacy_rows_block_migration_before_schema_changes()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        await SeedLegacyRowsAsync(context, duplicateUserNames: true);
        var organisationA = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Legacy', {{organisationA}});
            """);
        var migration = () => migrator.MigrateAsync("20260904124303_OrganisationTenantConstraints");
        await migration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Duplicate StaffMembers"));
        (await ScalarAsync<bool>(context,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'StaffMembers' AND column_name = 'TenantName')"))
            .Should().BeTrue();
        (await ScalarAsync<long>(context, """SELECT count(*) FROM "StaffMembers" """)).Should().Be(3);
        await AssertTenantMigrationRolledBackAsync(context);
    }

    [Fact]
    public async Task Reviewed_remediation_allows_backfill_and_tenant_constraints()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        await SeedLegacyRowsAsync(context, duplicateUserNames: true);
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Legacy', {{organisationA}});
            """);
        // Explicitly arrange both the invalid duplicate and its reviewed replacement key.
        await context.Database.ExecuteSqlRawAsync("""UPDATE "StaffMembers" SET "UserName" = 'staff-2' WHERE "Id" = 2""");
        await migrator.MigrateAsync("20260904124303_OrganisationTenantConstraints");
        (await ScalarAsync<long>(context, """SELECT count(*) FROM "StaffMembers" """)).Should().Be(3);
        (await ScalarAsync<long>(context, $"""SELECT count(*) FROM "StaffMembers" WHERE "OrganisationId" = '{organisationA}'"""))
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
            VALUES ('Other organisation', 'FIX', {{organisationB}}, 'test', now(), '', false);
            """);
        var duplicateInSameOrganisation = async () => await context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Teams" ("Name", "Acronym", "OrganisationId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete")
            VALUES ('Duplicate', 'FIX', {{organisationB}}, 'test', now(), '', false);
            """);
        await duplicateInSameOrganisation.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.SqlState == PostgresErrorCodes.UniqueViolation);
        var unsafeRollback = () => migrator.MigrateAsync("20250427103057_Initial Migration");
        await unsafeRollback.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Automatic rollback is disabled"));
    }

    [Fact]
    public async Task Missing_mapping_with_legacy_rows_blocks_migration_without_schema_changes()
    {
        await using var context = await CreateContextAsync();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        await SeedLegacyRowsAsync(context);
        var migration = () => migrator.MigrateAsync("20260904124303_OrganisationTenantConstraints");
        await migration.Should().ThrowAsync<PostgresException>()
            .Where(exception => exception.MessageText.Contains("Create and validate __OrganisationTenantMap"));
        (await ScalarAsync<long>(context, """SELECT count(*) FROM "StaffMembers" """)).Should().Be(3);
        await AssertTenantMigrationRolledBackAsync(context);
    }

    private static Task SeedLegacyRowsAsync(DeploymentDbContext context, bool duplicateUserNames = false) =>
        context.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "Teams"
              ("Id", "Name", "Acronym", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "TenantName")
            VALUES (1, 'Fixture team', 'FIX', 'test', timestamp '2026-01-01', '', false, 'Legacy');
            INSERT INTO "StaffMembers"
              ("Id", "FirstName", "LastName", "UserName", "TeamId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "TenantName")
            VALUES
              (1, 'First', 'Fixture', 'staff-1', 1, 'test', timestamp '2026-01-01', '', false, 'Legacy'),
              (2, 'Second', 'Fixture', {{(duplicateUserNames ? "staff-1" : "staff-2")}}, 1, 'test', timestamp '2026-01-01', '', false, 'Legacy'),
              (3, 'Third', 'Fixture', 'staff-3', 1, 'test', timestamp '2026-01-01', '', false, 'Legacy');
            SELECT setval(pg_get_serial_sequence('"Teams"', 'Id'), 1, true);
            SELECT setval(pg_get_serial_sequence('"StaffMembers"', 'Id'), 3, true);
            """);

    private static async Task AssertTenantMigrationRolledBackAsync(DeploymentDbContext context)
    {
        (await ScalarAsync<long>(context, """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND column_name = 'OrganisationId'
              AND table_name IN ('Teams', 'StaffMembers', 'Partners', 'ContactPersons', 'DocumentPartners', 'Emails')
            """)).Should().Be(0);
        (await ScalarAsync<string>(context,
            """SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1"""))
            .Should().Be("20250427103057_Initial Migration");
    }

    private async Task<DeploymentDbContext> CreateContextAsync()
    {
        var databaseName = $"admin_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(AdministratorConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(AdministratorConnectionString) { Database = databaseName };
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
