using ClientManagement.Core.Exceptions;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Application.IntegrationTests;

public sealed class NissPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => _postgres.StartAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canonical_identifier_round_trips_and_updates_through_the_guarded_property(bool useAsyncSave)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        var niss = SyntheticClient.Niss(year: 0, post2000: true);
        var client = SyntheticClient.Create(niss);
        client.AssignToOrganisation(Guid.NewGuid());
        context.Clients.Add(client);
        await SaveAsync(context, useAsyncSave);
        Assert.Equal(DateTimeKind.Utc, client.Created.Kind);
        context.ChangeTracker.Clear();

        var loaded = await context.Clients.IgnoreQueryFilters().SingleAsync();
        Assert.True(loaded.Ssn == niss, "PostgreSQL must preserve leading zeros and canonical digits.");
        Assert.Throws<InvalidNissFormatException>(() =>
            context.Entry(loaded).Property(c => c.Ssn).CurrentValue = "invalid-synthetic-input");
        Assert.True(loaded.Ssn == niss, "EF property updates must preserve the Domain invariant.");
        var replacement = SyntheticClient.Niss(month: 43);
        context.Entry(loaded).Property(c => c.Ssn).CurrentValue = replacement;
        await SaveAsync(context, useAsyncSave);
        Assert.Equal(DateTimeKind.Utc, loaded.LastModified!.Value.Kind);
        context.ChangeTracker.Clear();
        var updated = await context.Clients.IgnoreQueryFilters().SingleAsync();
        Assert.True(updated.Ssn == replacement);
        Assert.Equal(DateTimeKind.Utc, updated.Created.Kind);
        Assert.Equal(DateTimeKind.Utc, updated.LastModified!.Value.Kind);
    }

    [Fact]
    public async Task Legacy_invalid_value_cannot_materialize_as_a_valid_domain_entity()
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        var client = SyntheticClient.Create(SyntheticClient.Niss());
        client.AssignToOrganisation(Guid.NewGuid());
        context.Clients.Add(client);
        await context.SaveChangesAsync();
        // Simulates legacy prototype data, never an import supported by the application.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Clients\" SET \"Ssn\" = {"invalid-synthetic-input"} WHERE \"Id\" = {client.Id}");
        context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<InvalidNissFormatException>(async () =>
            await context.Clients.IgnoreQueryFilters().SingleAsync());
    }

    private static async Task SaveAsync(ApplicationDbContext context, bool useAsyncSave)
    {
        if (useAsyncSave)
            await context.SaveChangesAsync();
        else
            context.SaveChanges(CancellationToken.None);
    }

    private ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options);
}
