using System.Text.Json;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.EventBus.RabbitMq;

namespace Infrastructure.IntegrationTests;

// Uses EF InMemory for the publication boundary only, not PostgreSQL transactions/RLS or real broker proof.
public sealed class StaffProjectionPublicationBoundaryTests
{
    [Theory]
    [InlineData("mandatory-return")]
    [InlineData("negative-confirm")]
    [InlineData("uncertain-confirm")]
    public async Task Broker_failure_retains_pending_row_and_retries_the_original_event_identity(string outcome)
    {
        var fixture = new Fixture();
        var publisher = new Publisher { Publish = _ => Task.FromException(new RabbitMqPublishException(outcome)) };
        await fixture.SeedAsync();
        await using (var database = fixture.Open())
        {
            await fixture.Outbox(database, publisher).DispatchAsync(default);
            Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        }
        publisher.Publish = _ => Task.CompletedTask;
        await using var retry = fixture.Open();
        await fixture.Outbox(retry, publisher).DispatchAsync(default);
        Assert.NotNull((await retry.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        Assert.Equal(new[] { fixture.Message.Id, fixture.Message.Id }, publisher.Ids);
    }

    [Fact]
    public async Task Pending_confirmation_does_not_mark_published_before_success()
    {
        var fixture = new Fixture(); await fixture.SeedAsync();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher { Publish = _ => confirmation.Task };
        await using var database = fixture.Open();
        var dispatch = fixture.Outbox(database, publisher).DispatchAsync(default);
        try
        {
            await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
            Assert.False(dispatch.IsCompleted);
        }
        finally { confirmation.TrySetResult(); }
        await dispatch;
        Assert.NotNull((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
    }

    [Fact]
    public async Task Wait_timeout_and_late_confirm_leave_pending_until_explicit_same_identity_retry()
    {
        var fixture = new Fixture(); await fixture.SeedAsync();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher { Publish = _ => confirmation.Task };
        await using (var database = fixture.Open())
        {
            await fixture.Outbox(database, publisher).DispatchAsync(default);
            Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        }
        confirmation.TrySetResult(); // The transport may have accepted after the caller's bounded wait.
        await using (var observed = fixture.Open())
            Assert.Null((await observed.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        publisher.Publish = _ => Task.CompletedTask;
        await using var retry = fixture.Open();
        await fixture.Outbox(retry, publisher).DispatchAsync(default);
        Assert.NotNull((await retry.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        Assert.Equal(new[] { fixture.Message.Id, fixture.Message.Id }, publisher.Ids);
    }

    private sealed class Publisher : IEventBus
    {
        public Func<Event, Task> Publish { get; set; } = _ => Task.CompletedTask;
        public List<Guid> Ids { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(Event message) { Ids.Add(message.Id); Entered.TrySetResult(); return Publish(message); }
    }

    private sealed class Fixture
    {
        private readonly Guid organisation = Guid.NewGuid();
        private readonly DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        public StaffProjectionChangedV1 Message { get; }
        public Fixture() => Message = new(organisation, Guid.NewGuid(), 1, true, "Synthetic", "Worker", "synthetic", "Team", "SYN");
        public ApplicationDbContext Open() => new(options, TenantEnforcementTests.Scope(organisation));
        public async Task SeedAsync()
        {
            await using var database = Open();
            var row = new StaffProjectionMessage { EventId = Message.Id, Payload = JsonSerializer.Serialize(Message) };
            row.AssignToOrganisation(organisation); database.Add(row); await database.SaveChangesAsync();
        }
        public StaffProjectionOutbox Outbox(ApplicationDbContext database, Publisher publisher) => new(
            database, TenantEnforcementTests.Scope(organisation), publisher, NullLogger<StaffProjectionOutbox>.Instance,
            new ConfigurationBuilder().Build());
    }
}
