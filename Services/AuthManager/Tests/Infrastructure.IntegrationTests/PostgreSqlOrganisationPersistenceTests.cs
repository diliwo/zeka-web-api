using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
public sealed class PostgreSqlOrganisationPersistenceTests(PostgreSqlFixture fixture)
{
    private const string PrecedingMigration = "20260819155420_OnboardingOperationalFoundations";
    private const string OrganisationMigration = "20260902192150_OrganisationMembershipDomain";
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Organisation_migration_applies_from_preceding_schema_with_expected_constraints()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        await using var dbContext = CreateDbContext(connectionString);
        var migrator = dbContext.GetService<IMigrator>();

        await migrator.MigrateAsync(PrecedingMigration);
        (await dbContext.Database.GetAppliedMigrationsAsync()).Should().NotContain(OrganisationMigration);

        await migrator.MigrateAsync(OrganisationMigration);

        (await dbContext.Database.GetAppliedMigrationsAsync()).Should().Contain(OrganisationMigration);
        await AssertRelationExistsAsync(connectionString, "Organisations");
        await AssertRelationExistsAsync(connectionString, "OrganisationMemberships");
        await AssertRelationExistsAsync(connectionString, "PermissionSets");
        await AssertConstraintExistsAsync(connectionString, "FK_Organisations_AspNetUsers_OwnerUserId");
        await AssertConstraintExistsAsync(connectionString, "FK_OrganisationMemberships_AspNetUsers_UserId");
        await AssertConstraintExistsAsync(connectionString, "FK_OrganisationMemberships_Organisations_OrganisationId");
        await AssertConstraintExistsAsync(connectionString, "FK_OrganisationMemberships_PermissionSets_PermissionSetId");
        await AssertUniqueIndexExistsAsync(connectionString, "IX_OrganisationMemberships_UserId_OrganisationId");
        await AssertUniqueIndexExistsAsync(connectionString, "IX_PermissionSets_Code");
        await AssertColumnTypeAsync(connectionString, "Organisations", "ConcurrencyVersion", "bigint");
        await AssertColumnTypeAsync(connectionString, "OrganisationMemberships", "ConcurrencyVersion", "bigint");

        dbContext.Model.FindEntityType(typeof(Organisation))!
            .FindProperty(nameof(Organisation.ConcurrencyVersion))!.IsConcurrencyToken.Should().BeTrue();
        dbContext.Model.FindEntityType(typeof(OrganisationMembership))!
            .FindProperty(nameof(OrganisationMembership.ConcurrencyVersion))!.IsConcurrencyToken.Should().BeTrue();
    }

    [Fact]
    public async Task Competing_writers_cannot_create_duplicate_memberships()
    {
        var connectionString = await fixture.CreateDatabaseAsync();
        var organisationId = Guid.NewGuid();
        Guid userId;

        await using (var arrangeContext = CreateDbContext(connectionString))
        {
            await arrangeContext.Database.MigrateAsync();
            var owner = User.Create("owner@example.org", "owner", "Ada", "Lovelace", Now);
            userId = owner.Id;
            arrangeContext.Add(owner);
            arrangeContext.Organisations.Add(Organisation.Create(organisationId, "Example", userId, Now));
            await arrangeContext.SaveChangesAsync();
        }

        await using var firstContext = CreateDbContext(connectionString);
        await using var secondContext = CreateDbContext(connectionString);
        firstContext.OrganisationMemberships.Add(
            OrganisationMembership.CreateOwner(Guid.NewGuid(), organisationId, userId, Now));
        secondContext.OrganisationMemberships.Add(
            OrganisationMembership.Create(Guid.NewGuid(), organisationId, userId, PermissionSet.MemberId, Now));

        var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = SaveWhenReleasedAsync(firstContext, ready, start.Task);
        var secondWrite = SaveWhenReleasedAsync(secondContext, ready, start.Task);
        ready.Wait();
        start.SetResult();

        var results = await Task.WhenAll(firstWrite, secondWrite);

        results.Count(exception => exception is null).Should().Be(1);
        var failure = results.Single(exception => exception is not null);
        var dbUpdateException = failure.Should().BeOfType<DbUpdateException>().Which;
        dbUpdateException.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);

        await using var verificationContext = CreateDbContext(connectionString);
        (await verificationContext.OrganisationMemberships.CountAsync(
            membership => membership.UserId == userId && membership.OrganisationId == organisationId)).Should().Be(1);
    }

    private static AuthDbContext CreateDbContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new AuthDbContext(options);
    }

    private static async Task<Exception?> SaveWhenReleasedAsync(
        AuthDbContext dbContext,
        CountdownEvent ready,
        Task start)
    {
        ready.Signal();
        await start;

        try
        {
            await dbContext.SaveChangesAsync();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static Task AssertRelationExistsAsync(string connectionString, string relationName) =>
        AssertScalarAsync(connectionString, "SELECT to_regclass(@name) IS NOT NULL", $"\"{relationName}\"", true);

    private static Task AssertConstraintExistsAsync(string connectionString, string constraintName) =>
        AssertScalarAsync(
            connectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = @name)",
            constraintName,
            true);

    private static Task AssertUniqueIndexExistsAsync(string connectionString, string indexName) =>
        AssertScalarAsync(
            connectionString,
            "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @name AND indexdef LIKE 'CREATE UNIQUE INDEX%')",
            indexName,
            true);

    private static async Task AssertColumnTypeAsync(
        string connectionString,
        string tableName,
        string columnName,
        string expectedType)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @tableName AND column_name = @columnName
            """;
        command.Parameters.AddWithValue("tableName", tableName);
        command.Parameters.AddWithValue("columnName", columnName);

        (await command.ExecuteScalarAsync()).Should().Be(expectedType);
    }

    private static async Task AssertScalarAsync<T>(
        string connectionString,
        string sql,
        string parameterValue,
        T expected)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("name", parameterValue);

        (await command.ExecuteScalarAsync()).Should().Be(expected);
    }
}
