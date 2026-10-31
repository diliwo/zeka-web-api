using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class MembershipMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Membership_backfill_requires_reviewed_unambiguous_mapping_and_preserves_rows()
    {
        await using var database = new DeploymentDbContext(new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        await LegacyAdminAreaRows.SeedAsync(database);
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Legacy', {organisation});
            """);
        await migrator.MigrateAsync("20260907185334_CityReferenceIdentity");
        var missing = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165920_ExplicitTenantEnforcement"));
        Assert.Contains("__StaffMembershipMap", missing.MessageText);
        Assert.False(await database.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='StaffMembers' AND column_name='OrganisationMembershipId') AS "Value"
            """).SingleAsync());
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__StaffMembershipMap" ("LocalId" integer, "OrganisationId" uuid, "OrganisationMembershipId" uuid);
            INSERT INTO "__StaffMembershipMap" VALUES (1, {Guid.NewGuid()}, {membership}), (2, {organisation}, {Guid.NewGuid()}), (3, {organisation}, {Guid.NewGuid()});
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165920_ExplicitTenantEnforcement"));
        await database.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "__StaffMembershipMap" SET "OrganisationId" = {organisation}""");
        await database.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "__StaffMembershipMap" VALUES (1, {organisation}, {Guid.NewGuid()})""");
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165920_ExplicitTenantEnforcement"));
        await database.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "__StaffMembershipMap" WHERE "LocalId" = 1 AND "OrganisationMembershipId" <> {membership}""");
        await migrator.MigrateAsync("20260910165920_ExplicitTenantEnforcement");
        var worker = await database.StaffMembers.SingleAsync(x => x.Id == 1);
        Assert.Equal(membership, worker.OrganisationMembershipId); Assert.Equal("staff-1", worker.UserName);
        Assert.Equal(organisation, worker.OrganisationId); Assert.Equal(1, worker.ProjectionVersion);
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20260907185334_CityReferenceIdentity"));
        Assert.Contains("Automatic rollback is disabled", rollback.MessageText);
    }
}

internal static class LegacyAdminAreaRows
{
    // Deliberately explicit legacy rows: migration tests must not depend on demo model seeds.
    public static Task SeedAsync(DeploymentDbContext database) => database.Database.ExecuteSqlRawAsync("""
        INSERT INTO "Teams"
          ("Id", "Name", "Acronym", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "TenantName")
        VALUES (1, 'Fixture team', 'FIX', 'test', timestamp '2026-01-01', '', false, 'Legacy');
        INSERT INTO "StaffMembers"
          ("Id", "FirstName", "LastName", "UserName", "TeamId", "CreatedBy", "Created", "LastModifiedBy", "Softdelete", "TenantName")
        VALUES
          (1, 'First', 'Fixture', 'staff-1', 1, 'test', timestamp '2026-01-01', '', false, 'Legacy'),
          (2, 'Second', 'Fixture', 'staff-2', 1, 'test', timestamp '2026-01-01', '', false, 'Legacy'),
          (3, 'Third', 'Fixture', 'staff-3', 1, 'test', timestamp '2026-01-01', '', false, 'Legacy');
        SELECT setval(pg_get_serial_sequence('"Teams"', 'Id'), 1, true);
        SELECT setval(pg_get_serial_sequence('"StaffMembers"', 'Id'), 3, true);
        """);
}
