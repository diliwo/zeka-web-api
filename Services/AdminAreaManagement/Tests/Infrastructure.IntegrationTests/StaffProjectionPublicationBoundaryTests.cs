using System.Text.Json;
using AdminAreaManagement.Application.Common.Authorization;
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
        publisher = new Publisher { Ids = publisher.Ids };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wait_timeout_and_late_settlement_leave_pending_until_explicit_same_identity_retry(bool lateFailure)
    {
        var fixture = new Fixture(); await fixture.SeedAsync();
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new Publisher { Publish = _ => confirmation.Task };
        await using (var database = fixture.Open())
        {
            var dispatch = fixture.Outbox(database, publisher).DispatchAsync(default);
            await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                // Keep the original publication unsettled beyond the outcome deadline.
                await Assert.ThrowsAsync<TimeoutException>(() => dispatch.WaitAsync(TimeSpan.FromSeconds(3)));
            }
            finally
            {
                if (lateFailure) confirmation.TrySetException(new RabbitMqPublishException("uncertain-confirm"));
                else confirmation.TrySetResult();
            }
            await dispatch;
            Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        }
        await using (var observed = fixture.Open())
            Assert.Null((await observed.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        publisher = new Publisher { Ids = publisher.Ids };
        await using var retry = fixture.Open();
        await fixture.Outbox(retry, publisher).DispatchAsync(default);
        Assert.NotNull((await retry.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        Assert.Equal(new[] { fixture.Message.Id, fixture.Message.Id }, publisher.Ids);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_first_send_cannot_escape_the_transaction_gate_on_timeout_or_cancellation(bool cancel)
    {
        var fixture = new Fixture(); await fixture.SeedAsync();
        var gate = new TransactionGate();
        var preSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? publisherCommit = null;
        var publisher = new Publisher { Publish = async _ =>
        {
            await preSend.Task;
            publisherCommit!();
            Assert.False(gate.Closed); // This is FIRST send, not a late confirmation.
            sent.TrySetResult();
            await confirmation.Task;
        } };
        publisherCommit = () => publisher.Attempt!.CommitSend();
        using var cancellation = new CancellationTokenSource();
        await using var database = fixture.Open();
        var dispatch = gate.DispatchTransactionAsync(() =>
            fixture.Outbox(database, publisher, gate).DispatchAsync(cancellation.Token));
        await publisher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var close = gate.CloseTransactionAsync();
        await gate.ClosureRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) cancellation.Cancel();
        try
        {
            // A controlled pre-send barrier stays closed across cancellation/the real 2s deadline.
            // A bounded negative observation fails on the old code: closure becomes possible.
            await Assert.ThrowsAsync<TimeoutException>(() => close.WaitAsync(TimeSpan.FromSeconds(cancel ? 1 : 3)));
            Assert.False(dispatch.IsCompleted);
            Assert.False(sent.Task.IsCompleted);
            Assert.False(gate.Closed);
        }
        finally
        {
            preSend.TrySetResult();
            confirmation.TrySetResult();
        }
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatch);
        else await dispatch;
        await close.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(sent.Task.IsCompleted); // Stop denied the delayed FIRST send commitment.
        Assert.True(gate.Closed);
        Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.DispatchTransactionAsync(() =>
            fixture.Outbox(database, publisher, gate).DispatchAsync(default)));
        Assert.Equal(new[] { fixture.Message.Id }, publisher.Ids);
    }

    [Fact]
    public async Task Disabled_failstop_refuses_before_any_publication_admission()
    {
        var fixture = new Fixture(); await fixture.SeedAsync();
        var publisher = new Publisher { ProcessFailStopEnabled = false };
        await using var database = fixture.Open();
        await fixture.Outbox(database, publisher).DispatchAsync(default);
        Assert.Empty(publisher.Ids);
        Assert.Null((await database.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
    }

    // Models only the transaction-owned serialization contract, not PostgreSQL locking/RLS.
    private sealed class TransactionGate : IAdminAreaClosureGate
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        public bool Closed { get; private set; }
        public TaskCompletionSource ClosureRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DemandOrdinaryAccessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Closed) throw new InvalidOperationException("Closed organisation.");
            return Task.CompletedTask;
        }
        public async Task DispatchTransactionAsync(Func<Task> work)
        {
            await serial.WaitAsync();
            try { await work(); }
            finally { serial.Release(); }
        }
        public async Task CloseTransactionAsync()
        {
            ClosureRequested.TrySetResult();
            await serial.WaitAsync();
            try { Closed = true; }
            finally { serial.Release(); }
        }
    }

    private sealed class Publisher : IOwnedEventBus
    {
        public Func<Event, Task> Publish { get; set; } = _ => Task.CompletedTask;
        public List<Guid> Ids { get; init; } = [];
        private readonly PublicationLifecycle lifecycle = new(new PublisherFailStopOptions());
        public bool ProcessFailStopEnabled { get; init; } = true; // Modeled control, never actual FailFast here.
        public OwnedPublicationAttempt? Attempt { get; private set; }
        public IPublicationAttempt BeginPublication(Event message)
        {
            Ids.Add(message.Id);
            Attempt = lifecycle.Begin(async _ => { Entered.TrySetResult(); await Publish(message); });
            return Attempt;
        }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(Event message) => BeginPublication(message).Completion;
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
        public StaffProjectionOutbox Outbox(ApplicationDbContext database, Publisher publisher,
            IAdminAreaClosureGate? gate = null) => new(
            database, TenantEnforcementTests.Scope(organisation), publisher, NullLogger<StaffProjectionOutbox>.Instance,
            new ConfigurationBuilder().Build(), gate);
    }
}
