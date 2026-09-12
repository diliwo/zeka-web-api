using ClientManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

public sealed class PostgreSqlClientRoleCatalogEvidenceTests : IAsyncLifetime
{
    private const string MigratorPassword = "test-migrator-password";
    private const string RuntimePassword = "test-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task Fresh_database_migrates_through_owner_assumption_and_matches_client_manifest()
    {
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-client-roles.sql"));
        await ExecuteAdministratorAsync($"ALTER ROLE zeka_client_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_client_runtime PASSWORD '{RuntimePassword}';");

        var migratorConnection = Connection("zeka_client_migrator", MigratorPassword);
        await using (var deployment = Deployment(migratorConnection))
        {
            await deployment.Database.OpenConnectionAsync();
            await deployment.Database.ExecuteSqlRawAsync("SET ROLE zeka_client_owner");
            await deployment.Database.ExecuteSqlInterpolatedAsync($"""
                CREATE TABLE "__OrganisationTenantMap"
                  ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE)
                """);
            await deployment.GetService<IMigrator>().MigrateAsync();
        }

        await using var verification = Deployment(migratorConnection);
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(ApplicationDbContext).Assembly);
        await new RuntimeDatabaseIdentityValidator(Connection("zeka_client_runtime", RuntimePassword),
            "zeka_client_runtime").StartAsync(default);
    }

    private static DeploymentDbContext Deployment(string connectionString) => new(
        new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString).Options);

    private string Connection(string username, string password) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = username, Password = password }.ConnectionString;

    private async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadBootstrapScript(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed database role bootstrap script was not found.", fileName);
    }
}
