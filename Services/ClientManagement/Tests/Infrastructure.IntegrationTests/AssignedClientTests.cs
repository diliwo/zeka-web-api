using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class AssignedClientTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    private DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options;

    [Theory]
    [InlineData("other")] [InlineData("missing")] [InlineData("ambiguous")]
    [InlineData("closed")] [InlineData("inactive")] [InlineData("deleted")]
    public async Task Lists_details_and_detached_writes_require_one_current_active_assignment(string scenario)
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        int visibleId; int hiddenId;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation)))
        {
            var own = Worker(membership); var other = Worker(Guid.NewGuid());
            var visible = SyntheticClient.Create(SyntheticClient.Niss(sequence: 21));
            var hidden = SyntheticClient.Create(SyntheticClient.Niss(sequence: 22));
            hidden.ReferenceNumber = "hidden-reference";
            seed.AddRange(own, other, visible, hidden);
            seed.Add(new SocialCase(visible, DateTime.UtcNow, own));
            if (scenario != "missing")
            {
                var worker = scenario is "other" or "inactive" ? other : own;
                if (scenario == "inactive") worker.Softdelete = true;
                var support = new SocialCase(hidden, DateTime.UtcNow, worker);
                if (scenario == "closed") support.EndDate = DateTime.UtcNow;
                if (scenario == "deleted") support.Softdelete = true;
                seed.Add(support);
                if (scenario == "ambiguous") seed.Add(new SocialCase(hidden, DateTime.UtcNow, other));
            }
            await seed.SaveChangesAsync(); visibleId = visible.Id; hiddenId = hidden.Id;
        }
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new Access(organisation, membership), new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("Clients.EditAll", "Clients.EditAssigned"), default);
        await using var database = new ApplicationDbContext(Options, scope, operation);
        Assert.Equal(new[] { visibleId }, await database.Visible<Client>().Select(x => x.Id).ToArrayAsync());
        Assert.Null(await database.Visible<Client>().SingleOrDefaultAsync(x => x.Id == hiddenId));
        var allowed = await database.Visible<Client>().SingleAsync();
        var mapper = new AutoMapper.MapperConfiguration(configuration =>
            configuration.AddProfile<ClientManagement.Application.Common.Mappings.MappingProfile>()).CreateMapper();
        var detail = await new ClientManagement.Application.Clients.Queries.GetClientDetail.GetClientDetailQuery.GetClientDetailQueryHandler(
            new RepositoryManager(database), mapper).Handle(
                new ClientManagement.Application.Clients.Queries.GetClientDetail.GetClientDetailQuery { ClientId = visibleId }, default);
        Assert.Equal("synthetic@example.invalid", detail.Email);
        Assert.Equal("Test street", detail.Address.Street);
        allowed.FirstName = "Updated";
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
        var forged = SyntheticClient.Create(SyntheticClient.Niss(sequence: 22));
        forged.AssignToOrganisation(organisation);
        database.Entry(forged).Property(x => x.Id).CurrentValue = hiddenId;
        database.Attach(forged);
        database.Entry(forged).Property(x => x.FirstName).IsModified = true;
        await Assert.ThrowsAsync<TenantAccessException>(() => database.SaveChangesAsync());
    }

    [Fact]
    public async Task Flattened_optional_values_round_trip_and_retain_mutable_value_tracking()
    {
        var organisation = Guid.NewGuid();
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: 31));
        database.Add(client); await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        var loaded = await database.Clients.SingleAsync();
        Assert.Equal("Test street", loaded.Address!.Street);
        Assert.Equal("synthetic@example.invalid", loaded.Email!.EmailAddress);
        loaded.Email.EmailAddress = "changed@example.invalid";
        await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        Assert.Equal("changed@example.invalid", (await database.Clients.SingleAsync()).Email!.EmailAddress);
    }

    private static SocialWorker Worker(Guid membership)
    {
        var worker = new SocialWorker();
        worker.ApplyProjection(membership, 1, Guid.NewGuid(), true, "Synthetic", "Worker", membership.ToString(), "Team", "T");
        return worker;
    }
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "subject"; }
    private sealed class Access(Guid organisation, Guid membership) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken cancellationToken)
            => Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
                new(organisation, membership, new[] { "Clients.EditAssigned" }, "1", DateTimeOffset.UtcNow)));
    }
}
