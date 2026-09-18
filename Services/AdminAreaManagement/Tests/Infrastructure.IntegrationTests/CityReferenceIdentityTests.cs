using AdminAreaManagement.Application.Cities;
using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class CityPostgreSqlFixture : IAsyncLifetime
{
    public const string PreviousMigration = "20260904124303_OrganisationTenantConstraints";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    public async Task<DeploymentDbContext> CreateContextAsync(bool migrateCity = true, bool keepSeedCities = false)
    {
        var databaseName = $"city_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = databaseName };
        var context = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(connectionString.ConnectionString).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        // Existing issue #43 fixture remediation, confined to this disposable database.
        var organisationId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "StaffMembers" SET "UserName" = 'jdoe' WHERE "Id" = 1;
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {organisationId});
            """);
        await migrator.MigrateAsync(PreviousMigration);
        // Most behavioral tests own their City fixtures. Keep the historical seeds in a dedicated migration test.
        if (!keepSeedCities) await context.Database.ExecuteSqlRawAsync("DELETE FROM \"Cities\"");
        if (migrateCity) await migrator.MigrateAsync("20260907185334_CityReferenceIdentity");
        return context;
    }
}

public class CityReferenceIdentityTests(CityPostgreSqlFixture fixture) : IClassFixture<CityPostgreSqlFixture>
{
    [Fact]
    public async Task Existing_seed_cities_survive_the_migration_without_reclassification()
    {
        await using var context = await fixture.CreateContextAsync(keepSeedCities: true);
        var cities = await context.Cities.OrderBy(city => city.Id).ToListAsync();
        cities.Select(city => city.Name).Should().Equal("Brussels", "Oslo", "Cape Town");
        cities.Select(city => city.Country).Should().Equal("Belgium", "Norway", "South Africa");
        (await Queries(context).ActiveCityExistsAsync(" BRUSSELS ", "belgium", CancellationToken.None)).Should().BeTrue();
    }

        private static CityQueries Queries(DeploymentDbContext context)
    {
        var tenant = TenantEnforcementTests.Scope(Guid.NewGuid());
        return new CityQueries(new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options, tenant));
    }

    private static Task<int> InsertAsync(DeploymentDbContext context, string name, string country, bool deleted = false) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Cities" ("Name", "Country", "Created", "CreatedBy", "LastModifiedBy", "Softdelete")
            VALUES ({name}, {country}, timestamp '2026-09-07', '', '', {deleted});
            """);

    [Theory]
    [InlineData("  Lie\u0300ge\t", "BeLgIuM", "LIÈGE", " belgium ")]
    [InlineData("\u00a0Évry\u2003", " France ", "E\u0301VRY", "FRANCE")]
    [InlineData("Straße", "Germany", "STRASSE", "GERMANY")]
    [InlineData("Straße", "Germany", "STRAẞE", "GERMANY")]
    [InlineData("ος", "Ελλάδα", "ΟΣ", "ΕΛΛΆΔΑ")]
    [InlineData("\U00010428", "Country", "\U00010400", "COUNTRY")]
    public async Task NFC_trim_and_case_variants_share_one_global_active_key(
        string name, string country, string otherName, string otherCountry)
    {
        await using var context = await fixture.CreateContextAsync();
        var canonical = new City(name, country);
        await InsertAsync(context, canonical.Name, canonical.Country);
        (await Queries(context).ActiveCityExistsAsync(otherName, otherCountry, CancellationToken.None)).Should().BeTrue();

        var equivalent = new City(otherName, otherCountry);
        Func<Task> duplicate = () => InsertAsync(context, equivalent.Name, equivalent.Country);
        var exception = await duplicate.Should().ThrowAsync<PostgresException>();
        exception.Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        exception.Which.ConstraintName.Should().Be("UX_Cities_ActiveNormalizedIdentity");
        var city = await context.Cities.SingleAsync();
        city.Name.Should().Be(name.Normalize().Trim());
        city.Country.Should().Be(country.Normalize().Trim());
        context.Model.FindEntityType(typeof(City))!.FindProperty("OrganisationId").Should().BeNull();
    }

    [Theory]
    [InlineData("Evry", "France")]
    [InlineData("Évry", "Fránce")]
    [InlineData("É  vry", "France")]
    [InlineData("É vry", "France")]
    [InlineData("Évry", "Canada")]
    public async Task Accents_internal_whitespace_and_country_are_significant(string name, string country)
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Évry", "France");
        (await Queries(context).ActiveCityExistsAsync(name, country, CancellationToken.None)).Should().BeFalse();
        await InsertAsync(context, name, country);
        (await context.Cities.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Unicode_scalar_limit_is_enforced_after_NFC_and_trim_for_both_columns()
    {
        await using var context = await fixture.CreateContextAsync();
        var display = " \u00a0" + string.Concat(Enumerable.Repeat("e\u0301\U0001f600", 50)) + "\t";
        var city = new City(display, display);
        await InsertAsync(context, city.Name, city.Country);
        var persisted = await context.Cities.SingleAsync();
        var canonical = string.Concat(Enumerable.Repeat("é\U0001f600", 50));
        persisted.Name.Should().Be(canonical);
        persisted.Country.Should().Be(canonical);
        context.Model.FindEntityType(typeof(City))!.FindProperty("Name")!.GetMaxLength().Should().Be(100);
        context.Model.FindEntityType(typeof(City))!.FindProperty("Country")!.GetMaxLength().Should().Be(100);
        foreach (var invalid in new[] { new string('a', 101), string.Concat(Enumerable.Repeat("\U0001f600", 101)), "\t\u00a0", "" })
        {
            Func<Task> name = () => InsertAsync(context, invalid, "Belgium");
            Func<Task> country = () => InsertAsync(context, "Brussels", invalid);
            var nameFailure = (await name.Should().ThrowAsync<PostgresException>()).Which;
            var countryFailure = (await country.Should().ThrowAsync<PostgresException>()).Which;
            if (invalid.Length > 100)
            {
                nameFailure.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
                countryFailure.SqlState.Should().Be(PostgresErrorCodes.StringDataRightTruncation);
            }
            else
            {
                nameFailure.ConstraintName.Should().Be("CK_Cities_Name_Text");
                countryFailure.ConstraintName.Should().Be("CK_Cities_Country_Text");
            }
        }
    }

    [Fact]
    public async Task Every_outer_Unicode_whitespace_character_matches_the_domain_trim_policy()
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Brussels", "Belgium");
        foreach (var whitespace in "\u0009\u000a\u000b\u000c\u000d\u0020\u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000")
        {
            var value = $"{whitespace}Brussels{whitespace}";
            CityText.TryNormalize(value, out var normalized).Should().BeTrue();
            normalized.Should().Be("Brussels");
            (await Queries(context).ActiveCityExistsAsync(value, $"{whitespace}Belgium{whitespace}", CancellationToken.None)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Deleted_rows_allow_recreation_but_duplicate_restoration_is_rejected()
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Brussels", "Belgium", deleted: true);
        (await Queries(context).ActiveCityExistsAsync("BRUSSELS", "BELGIUM", CancellationToken.None)).Should().BeFalse();
        await InsertAsync(context, "BRUSSELS", "BELGIUM");

        Func<Task> restore = () => context.Database.ExecuteSqlRawAsync(
            """UPDATE "Cities" SET "Softdelete" = false WHERE "Name" = 'Brussels'""");
        (await restore.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        await context.Database.ExecuteSqlRawAsync("""UPDATE "Cities" SET "Softdelete" = true WHERE "Name" = 'BRUSSELS'""");
        await restore();
        (await context.Cities.CountAsync(c => !c.Softdelete)).Should().Be(1);
    }

    [Fact]
    public async Task Updating_display_text_recomputes_keys_and_rejects_a_duplicate()
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Brussels", "Belgium");
        await InsertAsync(context, "Antwerp", "Belgium");
        Func<Task> duplicate = () => context.Database.ExecuteSqlRawAsync(
            """UPDATE "Cities" SET "Name" = 'BRUSSELS' WHERE "Name" = 'Antwerp'""");
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        await context.Database.ExecuteSqlRawAsync("""UPDATE "Cities" SET "Name" = 'Gent' WHERE "Name" = 'Antwerp'""");
        (await Queries(context).ActiveCityExistsAsync("GENT", "BELGIUM", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_creates_cannot_both_commit_the_same_identity()
    {
        await using var context = await fixture.CreateContextAsync();
        await using var other = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options);
        (await Queries(context).ActiveCityExistsAsync("Brussels", "Belgium", CancellationToken.None)).Should().BeFalse();
        (await Queries(other).ActiveCityExistsAsync("brussels", "belgium", CancellationToken.None)).Should().BeFalse();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Write(DeploymentDbContext db, string name)
        {
            await gate.Task;
            try { await InsertAsync(db, name, "Belgium"); return "committed"; }
            catch (PostgresException exception) { return exception.SqlState; }
        }
        var first = Write(context, "Brussels");
        var second = Write(other, "BRUSSELS");
        gate.SetResult();
        (await Task.WhenAll(first, second)).Should().BeEquivalentTo("committed", PostgresErrorCodes.UniqueViolation);
        (await context.Cities.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Normalized_keys_cannot_be_forged_by_a_raw_writer()
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Brussels", "Belgium");
        Func<Task> forge = () => context.Database.ExecuteSqlRawAsync(
            """UPDATE "Cities" SET "NormalizedName" = 'FORGED'""");
        (await forge.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("428C9");
        (await Queries(context).ActiveCityExistsAsync("brussels", "belgium", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_restorations_cannot_both_commit_the_same_identity()
    {
        await using var context = await fixture.CreateContextAsync();
        await using var other = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(context.Database.GetConnectionString()).Options);
        await InsertAsync(context, "Brussels", "Belgium", deleted: true);
        await InsertAsync(context, "BRUSSELS", "Belgium", deleted: true);
        (await Queries(context).ActiveCityExistsAsync("Brussels", "Belgium", CancellationToken.None)).Should().BeFalse();
        (await Queries(other).ActiveCityExistsAsync("BRUSSELS", "Belgium", CancellationToken.None)).Should().BeFalse();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Restore(DeploymentDbContext db, string name)
        {
            await gate.Task;
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Cities" SET "Softdelete" = false WHERE "Name" = {name}""");
                return "committed";
            }
            catch (PostgresException exception) { return exception.SqlState; }
        }
        var first = Restore(context, "Brussels");
        var second = Restore(other, "BRUSSELS");
        gate.SetResult();
        (await Task.WhenAll(first, second)).Should().BeEquivalentTo("committed", PostgresErrorCodes.UniqueViolation);
        (await context.Cities.CountAsync(city => !city.Softdelete)).Should().Be(1);
    }

    [Fact]
    public async Task Query_port_is_cancellable_and_retains_filter_sort_and_paging()
    {
        await using var context = await fixture.CreateContextAsync();
        await InsertAsync(context, "Antwerp", "Belgium");
        await InsertAsync(context, "Brussels", "Belgium");
        await InsertAsync(context, "Ghent", "Belgium", deleted: true);
        await InsertAsync(context, "Paris", "France");
        var queries = Queries(context);
        var page = await queries.GetPageAsync(" BEL ", "Name desc", 2, 1, CancellationToken.None);
        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle().Which.Name.Should().Be("Antwerp");
        page.PageNumber.Should().Be(2);
        page.PageSize.Should().Be(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Func<Task> exists = () => queries.ActiveCityExistsAsync("Brussels", "Belgium", cancellation.Token);
        Func<Task> listing = () => queries.GetPageAsync("", "Name", 1, 10, cancellation.Token);
        await exists.Should().ThrowAsync<OperationCanceledException>();
        await listing.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_stops_on_invalid_legacy_data_and_can_be_retried_without_loss(bool duplicate)
    {
        await using var context = await fixture.CreateContextAsync(migrateCity: false);
        var name = duplicate ? "Brussels" : new string('a', 101);
        await InsertAsync(context, name, "Belgium");
        if (duplicate) await InsertAsync(context, " BRUSSELS ", "BELGIUM");
        var before = await context.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "Cities" """).SingleAsync();
        var beforeNames = await context.Database.SqlQuery<string>($"""SELECT "Name" AS "Value" FROM "Cities" ORDER BY "CityId" """).ToListAsync();
        Func<Task> migrate = () => context.GetService<IMigrator>().MigrateAsync("20260907185334_CityReferenceIdentity");
        var exception = await migrate.Should().ThrowAsync<PostgresException>();
        exception.Which.MessageText.Should().Contain("reviewed remediation");
        (await context.Database.SqlQuery<string>($"""SELECT "Name" AS "Value" FROM "Cities" ORDER BY "CityId" """).ToListAsync())
            .Should().Equal(beforeNames);
        (await context.Database.SqlQuery<int>($"""SELECT count(*)::int AS "Value" FROM "Cities" """).SingleAsync()).Should().Be(before);
        (await context.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM information_schema.columns
            WHERE table_name = 'Cities' AND column_name = 'NormalizedName'
            """).SingleAsync()).Should().Be(0);
        if (duplicate)
            await context.Database.ExecuteSqlRawAsync("""UPDATE "Cities" SET "Softdelete" = true WHERE "Name" = ' BRUSSELS '""");
        else
            await context.Database.ExecuteSqlRawAsync("""UPDATE "Cities" SET "Name" = 'Reviewed replacement'""");
        await migrate();
        (await context.Cities.CountAsync()).Should().Be(before);
    }

    [Fact]
    public async Task Migration_canonicalizes_legacy_text_and_rollback_preserves_the_canonical_values()
    {
        await using var context = await fixture.CreateContextAsync(migrateCity: false);
        await InsertAsync(context, " Lie\u0300ge ", " Belgium ");
        await InsertAsync(context, " LIE\u0300GE ", " BELGIUM ", deleted: true);
        await context.GetService<IMigrator>().MigrateAsync("20260907185334_CityReferenceIdentity");
        (await context.Cities.CountAsync()).Should().Be(2);
        var active = await context.Cities.SingleAsync(c => !c.Softdelete);
        active.Name.Should().Be("Liège");
        active.Country.Should().Be("Belgium");
        var deleted = await context.Cities.SingleAsync(c => c.Softdelete);
        deleted.Name.Should().Be("LIÈGE");
        deleted.Country.Should().Be("BELGIUM");
        (await Queries(context).ActiveCityExistsAsync("LIÈGE", "BELGIUM", CancellationToken.None)).Should().BeTrue();
        await context.GetService<IMigrator>().MigrateAsync(CityPostgreSqlFixture.PreviousMigration);
        (await context.Database.SqlQuery<string>($"""
            SELECT "Name" AS "Value" FROM "Cities" WHERE NOT "Softdelete"
            """).SingleAsync()).Should().Be("Liège");
        await context.GetService<IMigrator>().MigrateAsync("20260907185334_CityReferenceIdentity");
        (await context.Cities.CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(" Brussels ")]
    [InlineData("Lie\u0300ge")]
    [InlineData("\u00a0Paris\t")]
    public async Task Raw_inserts_and_updates_cannot_store_noncanonical_text(string noncanonical)
    {
        await using var context = await fixture.CreateContextAsync();
        Func<Task> name = () => InsertAsync(context, noncanonical, "Belgium");
        Func<Task> country = () => InsertAsync(context, "Brussels", noncanonical);
        (await name.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("CK_Cities_Name_Text");
        (await country.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("CK_Cities_Country_Text");
        await InsertAsync(context, "Brussels", "Belgium");
        Func<Task> updateName = () => context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Cities" SET "Name" = {noncanonical}""");
        Func<Task> updateCountry = () => context.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Cities" SET "Country" = {noncanonical}""");
        (await updateName.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("CK_Cities_Name_Text");
        (await updateCountry.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("CK_Cities_Country_Text");
        (await context.Cities.SingleAsync()).Name.Should().Be("Brussels");
    }

    [Fact]
    public async Task Migration_canonicalizes_before_narrowing_the_storage_length()
    {
        await using var context = await fixture.CreateContextAsync(migrateCity: false);
        var raw = new string(' ', 1000) + string.Concat(Enumerable.Repeat("e\u0301\U0001f600", 50)) + "\t";
        await InsertAsync(context, raw, raw);
        await context.GetService<IMigrator>().MigrateAsync("20260907185334_CityReferenceIdentity");
        var city = await context.Cities.SingleAsync();
        var canonical = string.Concat(Enumerable.Repeat("é\U0001f600", 50));
        city.Name.Should().Be(canonical);
        city.Country.Should().Be(canonical);
        var sizes = await context.Database.SqlQuery<int>($"""
            SELECT character_maximum_length AS "Value" FROM information_schema.columns
            WHERE table_name = 'Cities' AND column_name IN ('Name', 'Country')
            """).ToListAsync();
        sizes.Should().Equal(100, 100);
    }

    [Fact]
    public async Task Composition_resolves_the_semantic_port()
    {
        await using var context = await fixture.CreateContextAsync();
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = context.Database.GetConnectionString()
        });
        var services = new ServiceCollection();
        AdminAreaManagement.Application.DependencyInjection.AddApplication(services);
        AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<Zeka.Extensions.MultiTenancy.Abstractions.ITenantContextInitializer>().Establish(new Zeka.Extensions.MultiTenancy.Abstractions.TenantContext(new Zeka.Extensions.MultiTenancy.Abstractions.TenantId(Guid.NewGuid()), "fixture"));
        var queries = scope.ServiceProvider.GetRequiredService<ICityQueries>();
        var transactions = scope.ServiceProvider.GetRequiredService<
            AdminAreaManagement.Application.Common.Authorization.ITenantTransactionExecutor>();
        (await transactions.ExecuteAsync(token =>
            queries.ActiveCityExistsAsync("Missing", "Missing", token), CancellationToken.None)).Should().BeFalse();
    }
}
