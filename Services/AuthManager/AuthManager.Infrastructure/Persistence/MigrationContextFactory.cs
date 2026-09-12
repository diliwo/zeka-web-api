using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthManager.Infrastructure.Persistence;

// Design-time only. This placeholder never connects while scaffolding or generating SQL.
public sealed class MigrationContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ZEKA_MIGRATION_CONNECTION")
                ?? "Host=localhost;Database=zeka_migration_design;Username=deployment").Options);
}
