using ClientManagement.Infrastructure.Persistence;
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
        await migrator.MigrateAsync("20250425093851_Initial Migration");
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "SocialWorkers" ("FirstName", "LastName", "UserName", "TeamName", "TeamAcronym", "CreatedBy", "Created", "LastModifiedBy", "TenantName", "Softdelete")
                VALUES ('Synthetic', 'Worker', 'synthetic', 'Team', 'T', 'test', now(), '', 'Legacy', false);
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Legacy', {organisation});
            """);
        await migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints");
        var missing = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165923_ExplicitTenantEnforcement"));
        Assert.Contains("__StaffMembershipMap", missing.MessageText);
        Assert.False(await database.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='SocialWorkers' AND column_name='OrganisationMembershipId') AS "Value"
            """).SingleAsync());
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__StaffMembershipMap" ("LocalId" integer, "OrganisationId" uuid, "OrganisationMembershipId" uuid);
            INSERT INTO "__StaffMembershipMap" VALUES (1, {Guid.NewGuid()}, {membership});
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165923_ExplicitTenantEnforcement"));
        await database.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "__StaffMembershipMap" SET "OrganisationId" = {organisation}""");
        await database.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "__StaffMembershipMap" VALUES (1, {organisation}, {Guid.NewGuid()})""");
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165923_ExplicitTenantEnforcement"));
        await database.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "__StaffMembershipMap" WHERE "OrganisationMembershipId" <> {membership}""");
        await migrator.MigrateAsync("20260910165923_ExplicitTenantEnforcement");
        var worker = await database.SocialWorkers.SingleAsync();
        Assert.Equal(membership, worker.OrganisationMembershipId); Assert.Equal("synthetic", worker.UserName);
        Assert.Equal(organisation, worker.OrganisationId); Assert.Equal(0, worker.ProjectionVersion);
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20260904124417_OrganisationTenantConstraints"));
        Assert.Contains("Automatic rollback is disabled", rollback.MessageText);
    }
}
