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
        Assert.True(migrations.Length > 2);
        Assert.Equal(Predecessor, migrations[^3]);
        Assert.Equal(OnboardingTarget, migrations[^2]);
        Assert.Equal(OrganisationOwnerIndexContract.MigrationId, migrations[^1]);
        var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
        Assert.Equal(applied, migrations.Take(applied.Length)); // Unknown/gapped history is never repaired.
        if (applied.SequenceEqual(migrations, StringComparer.Ordinal))
        {
            Assert.True(await OrganisationOwnerIndexContract.IsInstalledAsync(database, cancellationToken));
            return;
        }
        if (applied.SequenceEqual(migrations[..^1], StringComparer.Ordinal))
        {
            await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner", cancellationToken);
            await database.GetService<IMigrator>().MigrateAsync(OrganisationOwnerIndexContract.MigrationId, cancellationToken);
            Assert.Empty(await database.Database.GetPendingMigrationsAsync(cancellationToken));
            return;
        }
        await database.GetService<IMigrator>().MigrateAsync(Predecessor, cancellationToken);
        Assert.Equal([OnboardingTarget, OrganisationOwnerIndexContract.MigrationId],
            await database.Database.GetPendingMigrationsAsync(cancellationToken));
        // Historical migrations reset the role; re-assume only the already admitted
        // owner at the pinned boundary, then apply every remaining migration.
        await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner", cancellationToken);
        await database.GetService<IMigrator>().MigrateAsync(OnboardingTarget, cancellationToken);
        await database.GetService<IMigrator>().MigrateAsync(OrganisationOwnerIndexContract.MigrationId, cancellationToken);
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(cancellationToken));
    }
}
