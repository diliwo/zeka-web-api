using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.ValueObjects;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class StaffOutboxTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    private DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Staff_and_event_commit_together_and_failed_publication_is_retryable_in_a_fresh_scope()
    {
        var organisation = Guid.NewGuid(); var scope = TenantEnforcementTests.Scope(organisation);
        var publisher = new Publisher { Fail = true };
        await using (var database = new ApplicationDbContext(Options, scope))
        {
            var team = new Team("Synthetic", "SYN"); database.Add(team); await database.SaveChangesAsync();
            var staff = new StaffMember("Synthetic", "Worker", team, "synthetic"); staff.LinkMembership(Guid.NewGuid());
            var outbox = new StaffProjectionOutbox(database, scope, publisher, NullLogger<StaffProjectionOutbox>.Instance);
            database.Add(staff); outbox.Stage(staff, team);
            await using (var transaction = await database.Database.BeginTransactionAsync())
            { await database.SaveChangesAsync(); await transaction.RollbackAsync(); }
            database.ChangeTracker.Clear();
            Assert.Empty(await database.StaffMembers.ToListAsync());
            Assert.Empty(await database.Set<StaffProjectionMessage>().ToListAsync());
            staff = new StaffMember("Synthetic", "Worker", team, "synthetic"); staff.LinkMembership(Guid.NewGuid());
            database.Attach(team); database.Add(staff); outbox.Stage(staff, team);
            await database.SaveChangesAsync(); await outbox.DispatchAsync(default);
            Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        }
        var otherScope = TenantEnforcementTests.Scope(Guid.NewGuid());
        await using (var other = new ApplicationDbContext(Options, otherScope))
            await new StaffProjectionOutbox(other, otherScope, publisher, NullLogger<StaffProjectionOutbox>.Instance).DispatchAsync(default);
        Assert.Equal(1, publisher.Calls);
        publisher.Fail = false;
        var retryScope = TenantEnforcementTests.Scope(organisation);
        await using var retry = new ApplicationDbContext(Options, retryScope);
        await new StaffProjectionOutbox(retry, retryScope, publisher, NullLogger<StaffProjectionOutbox>.Instance).DispatchAsync(default);
        Assert.NotNull((await retry.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        Assert.Equal(2, publisher.Calls);
        var persisted = await retry.StaffMembers.SingleAsync();
        retry.Entry(persisted).Property(x => x.OrganisationMembershipId).CurrentValue = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.SaveChangesAsync());
    }

    [Fact]
    public async Task Explicit_partner_dependents_preserve_values_and_reject_cross_tenant_reparenting()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(a));
        var team = new Team("Synthetic", "SYN");
        var staff = new StaffMember("Synthetic", "Worker", team, "synthetic"); staff.LinkMembership(Guid.NewGuid());
        var partner = new Partner { Name = "Synthetic", PartnerNumber = 1, StaffMember = staff,
            Address = new Address("1", "Street", "1000", "City"), CategoryOfPartnerName = "Synthetic",
            DateOfAgreementSignature = new DateTime(2026, 9, 10) };
        partner.Emails.Add(new Email("synthetic@example.invalid"));
        partner.ContactPersons.Add(new ContactPerson("synthetic-phone", "Synthetic", default));
        database.Add(partner); await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        var loaded = await database.Partners.Include(x => x.Emails).Include(x => x.ContactPersons).SingleAsync();
        Assert.Equal("Street", loaded.Address.Street);
        Assert.Equal(a, Assert.Single(loaded.Emails).OrganisationId);
        Assert.Equal(a, Assert.Single(loaded.ContactPersons).OrganisationId);
        loaded.Address.Street = "Changed"; await database.SaveChangesAsync(); database.ChangeTracker.Clear();
        Assert.Equal("Changed", (await database.Partners.SingleAsync()).Address.Street);
        await using var other = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(b));
        Assert.Empty(await other.Set<Email>().ToListAsync());
        Assert.Empty(await other.Set<ContactPerson>().ToListAsync());
        var forged = new Email("forged@example.invalid");
        other.Entry(forged).Property(x => x.PartnerId).CurrentValue = partner.Id;
        other.Add(forged);
        await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        other.ChangeTracker.Clear();
        other.Attach(loaded.Emails.Single());
        other.Entry(loaded.Emails.Single()).State = EntityState.Deleted;
        await Assert.ThrowsAsync<TenantContextException>(() => other.SaveChangesAsync());
    }
    private sealed class Publisher : IEventBus
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public Task PublishAsync(Event message)
        { Calls++; return Fail ? Task.FromException(new HttpRequestException("Synthetic outage")) : Task.CompletedTask; }
    }
}
