using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

public sealed class PermissionMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Legacy_member_never_receives_an_inferred_role_and_reviewed_mapping_preserves_membership()
    {
        await using var database = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync("20260902192150_OrganisationMembershipDomain");
        var now = DateTimeOffset.UtcNow;
        var user = User.Create("synthetic@example.invalid", "synthetic", "Test", "User", now);
        var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic", user.Id, now);
        var membership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, user.Id, PermissionSet.MemberId, now);
        database.AddRange(user, organisation, membership); await database.SaveChangesAsync();
        var missing = await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165430_TenantPermissionCatalogue"));
        Assert.Contains("__MembershipRoleMap", missing.MessageText);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__MembershipRoleMap" ("MembershipId" uuid, "OrganisationId" uuid, "RoleCode" text);
            INSERT INTO "__MembershipRoleMap" VALUES ({membership.Id}, {organisation.Id}, 'Admin');
            """);
        await Assert.ThrowsAsync<PostgresException>(() =>
            migrator.MigrateAsync("20260910165430_TenantPermissionCatalogue"));
        await database.Database.ExecuteSqlRawAsync("""UPDATE "__MembershipRoleMap" SET "RoleCode" = 'LimitedViewer'""");
        await migrator.MigrateAsync("20260910165430_TenantPermissionCatalogue");
        database.ChangeTracker.Clear();
        var migrated = await database.OrganisationMemberships.SingleAsync();
        Assert.Equal(membership.Id, migrated.Id); Assert.Equal(organisation.Id, migrated.OrganisationId);
        Assert.Equal(PermissionSet.LimitedViewerId, migrated.PermissionSetId);
        Assert.Equal(7, await database.PermissionSets.CountAsync(x => x.IsSystem));
        Assert.False((await database.PermissionSets.SingleAsync(x => x.Id == PermissionSet.MemberId)).IsSystem);
        var rollback = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20260902192150_OrganisationMembershipDomain"));
        Assert.Contains("Automatic rollback is disabled", rollback.MessageText);
    }
}
