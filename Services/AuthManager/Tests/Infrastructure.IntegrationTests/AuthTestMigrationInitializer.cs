using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Infrastructure.IntegrationTests;

// Same-project component-fixture initialization, not the production atomic executor.
// Callers retain their bootstrap, open connection, existing owner assumption and
// post-migration reconciliation/security assertions. No privilege is granted here.
internal static class AuthTestMigrationInitializer
{
    private const string Predecessor = "20261018144506_BrowserSessionSnapshot";
    private const string OnboardingTarget = "20261115195218_OnboardingRegistration";

    internal static async Task MigrateFreshDatabaseAsync(AuthDbContext database,
        CancellationToken cancellationToken = default)
    {
        var migrations = database.Database.GetMigrations().ToArray();
        Assert.True(migrations.Length > 1);
        Assert.Equal(Predecessor, migrations[^2]);
        Assert.Equal(OnboardingTarget, migrations[^1]);
        await database.GetService<IMigrator>().MigrateAsync(Predecessor, cancellationToken);
        Assert.Equal([OnboardingTarget], await database.Database.GetPendingMigrationsAsync(cancellationToken));
        // Historical migrations reset the role; re-assume only the already admitted
        // owner at the pinned boundary, then apply every remaining migration.
        await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner", cancellationToken);
        await database.GetService<IMigrator>().MigrateAsync(OnboardingTarget, cancellationToken);
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(cancellationToken));
    }
}
