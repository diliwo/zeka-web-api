using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Application.Supports.Commands.UpsertSupport;
using ClientManagement.Core.Entities;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class CurrentAssignmentTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    private DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options;

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Support_update_preserves_identity_even_when_worker_id_matches_another_client(bool all)
    {
        // The class fixture is shared by other tests. A fresh database makes the
        // two independently generated identity sequences deterministic here.
        var collisionDatabase = new TenantDatabase();
        await collisionDatabase.InitializeAsync();
        try
        {
        var collisionOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(collisionDatabase.ConnectionString).Options;
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        int supportId; int clientId; int otherClientId; int workerId;
        var start = DateTime.Today.AddDays(-4);
        await using (var seed = new ApplicationDbContext(collisionOptions, TenantEnforcementTests.Scope(organisation)))
        {
            var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: 81));
            var otherClient = SyntheticClient.Create(SyntheticClient.Niss(sequence: 82));
            otherClient.ReferenceNumber = "other-reference";
            var otherWorker = Worker(Guid.NewGuid(), "other");
            var worker = Worker(membership, "assigned");
            seed.AddRange(client, otherClient, otherWorker, worker);
            await seed.SaveChangesAsync();
            Assert.Equal(otherClient.Id, worker.Id); // Independent integer sequences collide.
            var earlier = new SocialCase(client, DateTime.Today.AddDays(-10), worker, "earlier")
                { EndDate = DateTime.Today.AddDays(-5) };
            seed.Add(earlier); await seed.SaveChangesAsync();
            var support = new SocialCase(client, start, worker, "original");
            seed.Add(support); await seed.SaveChangesAsync();
            supportId = support.Id; clientId = client.Id; otherClientId = otherClient.Id; workerId = worker.Id;
        }

        var scope = new TenantContextScope();
        var operation = await Operation(organisation, membership, all, scope);
        await using (var database = new ApplicationDbContext(collisionOptions, scope, operation))
        {
            var repository = new RepositoryManager(database);
            var handler = new UpsertSupportCommand.UpsertSupportCommandHandler(repository, null!, new LegacyIsolatedLock(database));
            var date = DateTime.Today.AddDays(-3);
            var matching = new UpsertSupportCommand { SupportId = supportId, ClientId = clientId,
                SocialWorkerId = workerId, StartDate = date, Note = "updated" };
            Assert.Equal(supportId, await handler.Handle(matching, default));
            database.ChangeTracker.Clear();

            var invalidDate = new UpsertSupportCommand { SupportId = supportId, ClientId = clientId,
                SocialWorkerId = workerId, StartDate = DateTime.Today.AddDays(-6), Note = "invalid-date" };
            await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(invalidDate, default));
            Assert.False(database.ChangeTracker.HasChanges());

            foreach (var mismatch in new[]
            {
                new UpsertSupportCommand { SupportId = supportId, ClientId = otherClientId,
                    SocialWorkerId = workerId, StartDate = date.AddDays(1), Note = "forged-client" },
                new UpsertSupportCommand { SupportId = supportId, ClientId = clientId,
                    SocialWorkerId = workerId + 1, StartDate = date.AddDays(1), Note = "forged-worker" }
            })
            {
                await Assert.ThrowsAsync<ValidationException>(() => handler.Handle(mismatch, default));
                Assert.False(database.ChangeTracker.HasChanges());
            }
        }

        await using var verify = new ApplicationDbContext(collisionOptions, TenantEnforcementTests.Scope(organisation));
        var saved = await verify.SocialCases.SingleAsync(s => s.Id == supportId);
        Assert.Equal(clientId, saved.ClientId);
        Assert.Equal(workerId, saved.SocialWorkerId);
        Assert.Equal(organisation, saved.OrganisationId);
        Assert.Equal("updated", saved.Note);
        Assert.Equal(0, await verify.SocialCases.CountAsync(s => s.ClientId == otherClientId));
        }
        finally { await collisionDatabase.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Hidden_or_missing_support_cannot_be_updated_or_disclosed(bool all)
    {
        var ownOrganisation = Guid.NewGuid(); var otherOrganisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        int hiddenId; int hiddenClientId; int hiddenWorkerId;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(otherOrganisation)))
        {
            var hidden = new SocialCase(SyntheticClient.Create(SyntheticClient.Niss(sequence: 83)),
                DateTime.Today.AddDays(-2), Worker(Guid.NewGuid(), "hidden"), "original");
            seed.Add(hidden); await seed.SaveChangesAsync();
            hiddenId = hidden.Id; hiddenClientId = hidden.ClientId; hiddenWorkerId = hidden.SocialWorkerId;
        }
        var scope = new TenantContextScope();
        var operation = await Operation(ownOrganisation, membership, all, scope);
        await using (var database = new ApplicationDbContext(Options, scope, operation))
        {
            var handler = new UpsertSupportCommand.UpsertSupportCommandHandler(new RepositoryManager(database), null!, new LegacyIsolatedLock(database));
            foreach (var id in new[] { hiddenId, int.MaxValue })
            {
                var command = new UpsertSupportCommand { SupportId = id, ClientId = hiddenClientId,
                    SocialWorkerId = hiddenWorkerId, StartDate = DateTime.Today, Note = "forged" };
                var error = await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(command, default));
                Assert.Contains("SocialCase", error.Message);
                Assert.False(database.ChangeTracker.HasChanges());
            }
        }
        await using var verify = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(otherOrganisation));
        Assert.Equal("original", (await verify.SocialCases.SingleAsync(s => s.Id == hiddenId)).Note);
    }

    [Fact]
    public async Task PostgreSql_failure_rolls_back_support_update_without_partial_mutation()
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        int supportId; int clientId; int workerId;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation)))
        {
            var support = new SocialCase(SyntheticClient.Create(SyntheticClient.Niss(sequence: 84)),
                DateTime.Today.AddDays(-4), Worker(membership, "assigned"), "original");
            seed.Add(support);
            await seed.SaveChangesAsync();
            supportId = support.Id; clientId = support.ClientId; workerId = support.SocialWorkerId;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var function = $"issue74_fail_{suffix}";
        var trigger = $"issue74_fail_{suffix}";
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION "{function}"() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."SchoolRegistrationId" = {supportId} THEN
                RAISE EXCEPTION 'synthetic persistence failure';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER "{trigger}" BEFORE UPDATE ON "SocialCases"
              FOR EACH ROW EXECUTE FUNCTION "{function}"();
            """, connection))
            await install.ExecuteNonQueryAsync();

        try
        {
            var scope = new TenantContextScope();
            var operation = await Operation(organisation, membership, true, scope);
            await using var database = new ApplicationDbContext(Options, scope, operation);
            var command = new UpsertSupportCommand { SupportId = supportId, ClientId = clientId,
                SocialWorkerId = workerId, StartDate = DateTime.Today.AddDays(-2), Note = "should-rollback" };
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                new UpsertSupportCommand.UpsertSupportCommandHandler(new RepositoryManager(database), null!, new LegacyIsolatedLock(database))
                    .Handle(command, default));
        }
        finally
        {
            await using var remove = new NpgsqlCommand($"""
                DROP TRIGGER "{trigger}" ON "SocialCases";
                DROP FUNCTION "{function}"();
                """, connection);
            await remove.ExecuteNonQueryAsync();
        }

        await using var verify = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var saved = await verify.SocialCases.SingleAsync(s => s.Id == supportId);
        Assert.Equal(clientId, saved.ClientId);
        Assert.Equal(workerId, saved.SocialWorkerId);
        Assert.Equal(DateTime.Today.AddDays(-4), saved.StartDate.Date);
        Assert.Equal("original", saved.Note);
        Assert.Equal(1, await verify.SocialCases.CountAsync());
    }

    [Theory]
    [InlineData("open", true)] [InlineData("future-end", true)] [InlineData("future-start", false)]
    [InlineData("ended", false)] [InlineData("deleted", false)] [InlineData("zero", false)]
    [InlineData("multiple", false)] [InlineData("inactive", false)]
    public async Task Reads_writes_and_mysupports_use_the_same_current_assignment(string scenario, bool allowed)
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid(); int id;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation)))
        {
            var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: 71));
            var worker = Worker(membership, "not-System");
            seed.AddRange(client, worker);
            if (scenario != "zero")
            {
                var support = new SocialCase(client, DateTime.Today.AddDays(-1), worker);
                if (scenario == "future-start") support.StartDate = DateTime.Today.AddDays(1);
                if (scenario == "future-end") support.EndDate = DateTime.Today.AddDays(1);
                if (scenario == "ended") support.EndDate = DateTime.Today;
                if (scenario == "deleted") support.Softdelete = true;
                if (scenario == "inactive") worker.Softdelete = true;
                seed.Add(support);
                if (scenario == "multiple") seed.Add(new SocialCase(client, DateTime.Today.AddDays(-2), worker));
            }
            await seed.SaveChangesAsync(); id = client.Id;
        }
        var scope = new TenantContextScope();
        var operation = await Operation(organisation, membership, false, scope);
        await using var database = new ApplicationDbContext(Options, scope, operation);
        Assert.Equal(allowed, await database.Visible<Client>().AnyAsync(x => x.Id == id));
        Assert.Equal(allowed, await new SupportRepository(database)
            .GetConsultantSupportsByMembership(operation.MembershipId).AnyAsync(x => x.ClientId == id));
        var changed = SyntheticClient.Create(SyntheticClient.Niss(sequence: 71));
        changed.AssignToOrganisation(organisation);
        database.Entry(changed).Property(x => x.Id).CurrentValue = id;
        database.Attach(changed);
        changed.FirstName = "Updated";
        database.Entry(changed).Property(x => x.FirstName).IsModified = true;
        if (allowed) await database.SaveChangesAsync();
        else await Assert.ThrowsAsync<TenantAccessException>(() => database.SaveChangesAsync());
    }

    [Theory]
    [InlineData("open", true)] [InlineData("future-end", true)]
    [InlineData("future-start", false)] [InlineData("ended", false)]
    public async Task Support_persistence_uses_the_canonical_rule_when_closing_the_previous_support(string scenario, bool current)
    {
        var organisation = Guid.NewGuid();
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: 77));
        var worker = Worker(Guid.NewGuid(), "renamed");
        var previous = new SocialCase(client,
            scenario == "future-start" ? DateTime.Today.AddDays(1) : DateTime.Today.AddDays(-1), worker);
        previous.EndDate = scenario == "future-end" ? DateTime.Today.AddDays(3)
            : scenario == "ended" ? DateTime.Today : null;
        var originalEnd = previous.EndDate;
        database.Add(previous);
        await database.SaveChangesAsync();
        var next = new SocialCase(client, DateTime.Today.AddDays(7), worker);
        new SupportRepository(database).Persist(next);
        await using var verification = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var persisted = await verification.SocialCases.SingleAsync(x => x.Id == previous.Id);
        Assert.Equal(current ? next.StartDate.AddDays(-1) : originalEnd, persisted.EndDate);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Mysupports_uses_authorized_membership_even_for_all_scope_and_misleading_usernames(bool all)
    {
        var organisation = Guid.NewGuid(); var otherOrganisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        int mine; int theirs;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation)))
        {
            var ownWorker = Worker(membership, "renamed-user"); var otherWorker = Worker(Guid.NewGuid(), "System");
            var ownClient = SyntheticClient.Create(SyntheticClient.Niss(sequence: 72));
            var otherClient = SyntheticClient.Create(SyntheticClient.Niss(sequence: 73));
            otherClient.ReferenceNumber = "other";
            seed.AddRange(new SocialCase(ownClient, DateTime.Today.AddDays(-1), ownWorker),
                new SocialCase(otherClient, DateTime.Today.AddDays(-1), otherWorker));
            await seed.SaveChangesAsync(); mine = ownClient.Id; theirs = otherClient.Id;
        }
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(otherOrganisation)))
        {
            seed.Add(new SocialCase(SyntheticClient.Create(SyntheticClient.Niss(sequence: 74)),
                DateTime.Today.AddDays(-1), Worker(membership, "renamed-user")));
            await seed.SaveChangesAsync();
        }
        var scope = new TenantContextScope(); var operation = await Operation(organisation, membership, all, scope);
        await using var database = new ApplicationDbContext(Options, scope, operation);
        Assert.Equal(all ? 2 : 1, await database.Visible<Client>().CountAsync());
        var repository = new SupportRepository(database);
        Assert.Equal(new[] { mine }, await repository.GetConsultantSupportsByMembership(operation.MembershipId)
            .Select(x => x.ClientId).ToArrayAsync());
        Assert.Empty(await repository.GetConsultantSupportsByMembership(Guid.Empty).ToListAsync());
        Assert.False(await repository.GetConsultantSupportsByMembership(operation.MembershipId).AnyAsync(x => x.ClientId == theirs));
    }

    [Fact]
    public async Task Missing_membership_link_cannot_be_persisted_or_used_for_mysupports()
    {
        var organisation = Guid.NewGuid();
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var worker = new SocialWorker("Synthetic", "Worker", "Team", "T", "System");
        database.Add(new SocialCase(SyntheticClient.Create(SyntheticClient.Niss(sequence: 76)),
            DateTime.Today, worker));
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
        database.ChangeTracker.Clear();
        Assert.Empty(await new SupportRepository(database).GetConsultantSupportsByMembership(Guid.Empty).ToListAsync());
    }

    [Fact]
    public async Task Cross_organisation_worker_link_is_rejected_by_postgresql()
    {
        var organisation = Guid.NewGuid(); var other = Guid.NewGuid(); int workerId;
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(other)))
        {
            var worker = Worker(Guid.NewGuid(), "System"); seed.Add(worker); await seed.SaveChangesAsync(); workerId = worker.Id;
        }
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        database.Add(new SocialCase { Client = SyntheticClient.Create(SyntheticClient.Niss(sequence: 75)),
            SocialWorkerId = workerId, StartDate = DateTime.Today });
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }

    private static SocialWorker Worker(Guid membership, string name)
    {
        var worker = new SocialWorker();
        worker.ApplyProjection(membership, 1, Guid.NewGuid(), true, "Synthetic", "Worker", name, "Team", "T");
        return worker;
    }
    private static async Task<TenantOperation> Operation(Guid organisation, Guid membership, bool all, TenantContextScope scope)
    {
        var operation = new TenantOperation(new Access(organisation, membership, all), new Identity(organisation), scope, scope);
        await operation.AuthorizeAsync(new("Clients.EditAll", "Clients.EditAssigned"), default);
        return operation;
    }
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "subject"; }
    private sealed record Access(Guid Organisation, Guid Membership, bool All) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken token) =>
            Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
                new(Organisation, Membership, new[] { All ? "Clients.EditAll" : "Clients.EditAssigned" }, "1", DateTimeOffset.UtcNow)));
    }
}

// Existing direct-handler regression tests use a provider but do not exercise the guarded
// transaction pipeline. Lock-order and two-connection behavior have separate tests.
internal sealed class LegacyIsolatedLock(ApplicationDbContext database) : IClientParentLock
{
    public Task<int?> ResolveCaseClientAsync(int socialCaseId, CancellationToken cancellationToken) =>
        database.SocialCases.Where(x => x.Id == socialCaseId).Select(x => (int?)x.ClientId)
            .SingleOrDefaultAsync(cancellationToken);
    public Task<int?> ResolveActionClientAsync(int actionId, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(null);
    public Task LockAsync(int clientId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task LockWorkerAsync(int socialWorkerId, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task LockActionAsync(int actionId, CancellationToken cancellationToken) => Task.CompletedTask;
}
