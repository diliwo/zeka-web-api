using ClientManagement.Infrastructure.Persistence;
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
    public async Task Migration_applies_after_validated_mapping_and_creates_tenant_constraints()
    {
        await using var context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(_postgres.GetConnectionString()).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250425093851_Initial Migration");
        await context.Database.ExecuteSqlRawAsync(
            "CREATE TABLE \"__OrganisationTenantMap\" (\"TenantName\" text PRIMARY KEY, \"OrganisationId\" uuid NOT NULL UNIQUE)");

        await migrator.MigrateAsync();

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

    private static async Task<T> ScalarAsync<T>(ApplicationDbContext context, string sql)
    {
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
