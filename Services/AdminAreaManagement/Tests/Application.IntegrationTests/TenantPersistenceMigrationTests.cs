using AdminAreaManagement.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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
    public async Task Migration_backfills_ids_and_rejects_cross_organisation_relationships()
    {
        await using var context = CreateContext();
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisationA = Guid.NewGuid();
        var organisationB = Guid.NewGuid();

        await context.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {{organisationA}});
            UPDATE "StaffMembers" SET "UserName" = 'jdoe' WHERE "Id" = 1;
            """);
        await migrator.MigrateAsync();

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
    }

    private ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);
}
