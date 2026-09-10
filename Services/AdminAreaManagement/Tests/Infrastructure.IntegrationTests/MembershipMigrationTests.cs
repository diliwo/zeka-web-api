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
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "StaffMembers" SET "UserName" = 'jdoe' WHERE "Id" = 1;
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {organisation});
            """);
        await migrator.MigrateAsync("20260907185334_CityReferenceIdentity");
        var missing = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
        Assert.Contains("__StaffMembershipMap", missing.MessageText);
        Assert.False(await database.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM information_schema.columns WHERE table_name='StaffMembers' AND column_name='OrganisationMembershipId') AS "Value"
            """).SingleAsync());
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__StaffMembershipMap" ("LocalId" integer, "OrganisationId" uuid, "OrganisationMembershipId" uuid);
            INSERT INTO "__StaffMembershipMap" VALUES (1, {Guid.NewGuid()}, {membership}), (2, {organisation}, {Guid.NewGuid()}), (3, {organisation}, {Guid.NewGuid()});
            """);
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
        await database.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "__StaffMembershipMap" SET "OrganisationId" = {organisation}""");
        await database.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "__StaffMembershipMap" VALUES (1, {organisation}, {Guid.NewGuid()})""");
        await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
        await database.Database.ExecuteSqlInterpolatedAsync($"""DELETE FROM "__StaffMembershipMap" WHERE "LocalId" = 1 AND "OrganisationMembershipId" <> {membership}""");
        await migrator.MigrateAsync();
        var worker = await database.StaffMembers.SingleAsync(x => x.Id == 1);
        Assert.Equal(membership, worker.OrganisationMembershipId); Assert.Equal("jdoe", worker.UserName);
        Assert.Equal(organisation, worker.OrganisationId); Assert.Equal(1, worker.ProjectionVersion);
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20260907185334_CityReferenceIdentity"));
        Assert.Contains("Automatic rollback is disabled", rollback.MessageText);
    }
}
