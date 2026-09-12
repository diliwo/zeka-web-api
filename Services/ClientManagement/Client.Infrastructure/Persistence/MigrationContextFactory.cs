using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClientManagement.Infrastructure.Persistence;

// Design-time only. This placeholder never connects while scaffolding or generating SQL.
public sealed class MigrationContextFactory : IDesignTimeDbContextFactory<DeploymentDbContext>
{
    public DeploymentDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<DeploymentDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ZEKA_MIGRATION_CONNECTION")
                ?? "Host=localhost;Database=zeka_migration_design;Username=deployment").Options);
}
