using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class TenantDatabase : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public string ConnectionString => postgres.GetConnectionString();
    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var deployment = new DeploymentDbContext(
            new DbContextOptionsBuilder<DeploymentDbContext>().UseNpgsql(ConnectionString).Options);
        var migrator = deployment.GetService<IMigrator>();
        await migrator.MigrateAsync("20250427103057_Initial Migration");
        var organisation = Guid.NewGuid();
        await deployment.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "StaffMembers" SET "UserName" = 'jdoe' WHERE "Id" = 1;
            CREATE TABLE "__OrganisationTenantMap" ("TenantName" text PRIMARY KEY, "OrganisationId" uuid NOT NULL UNIQUE);
            INSERT INTO "__OrganisationTenantMap" VALUES ('Zeka', {organisation});
            """);
        await migrator.MigrateAsync("20260904124303_OrganisationTenantConstraints");
        // Explicit synthetic membership identities for the three historical fixture records.
        await deployment.Database.ExecuteSqlInterpolatedAsync($"""
            CREATE TABLE "__StaffMembershipMap" ("LocalId" integer PRIMARY KEY, "OrganisationId" uuid NOT NULL, "OrganisationMembershipId" uuid NOT NULL);
            INSERT INTO "__StaffMembershipMap" VALUES
              (1, {organisation}, {Guid.NewGuid()}), (2, {organisation}, {Guid.NewGuid()}), (3, {organisation}, {Guid.NewGuid()});
            """);
        await migrator.MigrateAsync();
    }
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();
}

public sealed class TenantEnforcementTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    private DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString + ";Maximum Pool Size=2").Options;
    internal static TenantContextScope Scope(Guid organisation)
    {
        var scope = new TenantContextScope();
        scope.Establish(new TenantContext(new TenantId(organisation), "test-subject"));
        return scope;
    }
    private ApplicationDbContext Context(Guid organisation) => new(Options, Scope(organisation));


    [Fact]
    public void Every_persisted_type_has_exactly_one_classification_and_every_tenant_root_has_a_filter()
    {
        using var database = Context(Guid.NewGuid());
        foreach (var type in database.Model.GetEntityTypes())
        {
            Assert.False(type.IsOwned());
            var tenant = typeof(ITenantOwnedEntity).IsAssignableFrom(type.ClrType);
            Assert.NotEqual(tenant, typeof(IGlobalEntity).IsAssignableFrom(type.ClrType));
            if (tenant)
            {
                Assert.True(type.FindProperty("OrganisationId")!.IsConcurrencyToken);
                if (type.BaseType is null) Assert.NotNull(type.GetQueryFilter());
            }
        }
    }

    [Fact]
    public async Task Reads_and_new_rows_are_scoped_and_missing_context_fails_closed()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        foreach (var id in new[] { a, b })
        {
            await using var database = Context(id);
            var row = new Team("Team", Guid.NewGuid().ToString("N")[..6]);
            database.Add(row);
            await database.SaveChangesAsync();
            Assert.Equal(id, row.OrganisationId);
            Assert.Equal(id, (await database.Teams.SingleAsync()).OrganisationId);
        }
        await using var missing = new ApplicationDbContext(Options, new TenantContextScope());
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => missing.Teams.ToListAsync());
        missing.Add(new Team("Team", Guid.NewGuid().ToString("N")[..6]));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => missing.SaveChangesAsync());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task All_save_overloads_reject_forged_tenant_ids_and_mutation(int mode)
    {
        var a = Guid.NewGuid();
        await using var database = Context(a);
        var row = new Team("Team", Guid.NewGuid().ToString("N")[..6]);
        row.AssignToOrganisation(Guid.NewGuid());
        database.Add(row);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Save(database, mode));
        database.ChangeTracker.Clear();
        row = new Team("Team", Guid.NewGuid().ToString("N")[..6]);
        database.Add(row);
        await Save(database, mode);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            database.Entry(row).Property("OrganisationId").CurrentValue = Guid.NewGuid();
            await Save(database, mode);
        });
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Detached_cross_tenant_writes_cannot_match_another_organisations_row(bool delete)
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var row = new Team("Team", Guid.NewGuid().ToString("N")[..6]);
        await using (var owner = Context(a)) { owner.Add(row); await owner.SaveChangesAsync(); }
        await using var attacker = Context(b);
        var forged = new Team("Team", Guid.NewGuid().ToString("N")[..6]);
        attacker.Entry(forged).Property("Id").CurrentValue = row.Id;
        forged.AssignToOrganisation(b);
        if (delete) attacker.Remove(forged); else attacker.Update(forged);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => attacker.SaveChangesAsync());
        await using var verify = Context(a);
        Assert.Equal(row.Id, (await verify.Teams.SingleAsync()).Id);
    }

    [Fact]
    public async Task Raw_sql_bulk_operations_and_filter_bypass_are_rejected()
    {
        await using var database = Context(Guid.NewGuid());
        await Assert.ThrowsAsync<TenantContextException>(() => database.Teams.IgnoreQueryFilters().ToListAsync());
        await Assert.ThrowsAsync<TenantContextException>(() => database.Teams.ExecuteDeleteAsync());
        await Assert.ThrowsAsync<TenantContextException>(() => database.Teams.ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "forged")));
        await Assert.ThrowsAsync<TenantContextException>(() => database.Teams.FromSqlRaw("SELECT * FROM \"Teams\"").ToListAsync());
        await Assert.ThrowsAsync<TenantContextException>(() => database.Database.ExecuteSqlRawAsync("SELECT 1"));
        await Assert.ThrowsAsync<TenantContextException>(() => database.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").ToListAsync());
    }

    [Fact]
    public async Task Parallel_non_http_scopes_do_not_leak_through_reused_provider_connections()
    {
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var id = Guid.NewGuid();
            await using var database = Context(id);
            database.Add(new Team("Team", Guid.NewGuid().ToString("N")[..6]));
            await database.SaveChangesAsync();
            Assert.Equal(id, (await database.Teams.SingleAsync()).OrganisationId);
        }));
    }

    [Fact]
    public async Task Platform_access_requires_an_explicit_successfully_audited_capability()
    {
        var audit = new Audit();
        await Assert.ThrowsAsync<TenantContextException>(async () =>
            await PlatformAccessContext.AuthorizeAsync("operator", "investigation", new Policy(false), audit));
        Assert.Equal(0, audit.Count);
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        foreach (var id in new[] { a, b })
        {
            await using var database = Context(id);
            database.Add(new Team("Team", Guid.NewGuid().ToString("N")[..6]));
            await database.SaveChangesAsync();
        }
        var capability = await PlatformAccessContext.AuthorizeAsync("operator", "investigation", new Policy(true), audit);
        await using var platform = new ApplicationDbContext(Options, capability);
        Assert.Equal(2, await platform.Teams.CountAsync(x => x.OrganisationId == a || x.OrganisationId == b));
        Assert.Equal(1, audit.Count);
        await Assert.ThrowsAsync<TenantContextException>(() => platform.Teams.IgnoreQueryFilters().ToListAsync());
    }

    private static async Task Save(ApplicationDbContext database, int mode)
    {
        switch (mode)
        {
            case 0: database.SaveChanges(); break;
            case 1: database.SaveChanges(true); break;
            case 2: await database.SaveChangesAsync(); break;
            default: await database.SaveChangesAsync(true); break;
        }
    }
    private sealed class Policy(bool allow) : IPlatformAccessPolicy
    {
        public ValueTask<bool> AuthorizeAsync(string subjectId, string purpose, CancellationToken cancellationToken)
            => ValueTask.FromResult(allow);
    }
    private sealed class Audit : IPlatformAccessAudit
    {
        public int Count { get; private set; }
        public ValueTask RecordGrantAsync(string subjectId, string purpose, CancellationToken cancellationToken)
        { Count++; return ValueTask.CompletedTask; }
    }
}
