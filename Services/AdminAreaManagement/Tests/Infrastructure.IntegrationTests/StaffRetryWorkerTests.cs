using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class StaffRetryWorkerTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    private DbContextOptions<ApplicationDbContext> Options => new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString).Options;

    [Fact]
    public async Task Hosted_worker_autonomously_recovers_pending_tombstone_and_emits_redacted_failures()
    {
        var organisation = Guid.NewGuid(); var other = Guid.NewGuid(); var membership = Guid.NewGuid();
        await Seed(organisation, membership, false);
        await Seed(other, Guid.NewGuid(), false);
        var publisher = new Publisher();
        var authority = new Authority();
        var config = Configuration(organisation);
        var logs = new Logs();
        var outcomes = new ConcurrentBag<string>();
        using var metrics = new MeterListener();
        metrics.InstrumentPublished = (instrument, listener) =>
        { if (instrument.Meter.Name == StaffProjectionOutbox.DiagnosticsName) listener.EnableMeasurementEvents(instrument); };
        metrics.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        { foreach (var tag in tags) if (tag.Key == "outcome") outcomes.Add(tag.Value!.ToString()!); });
        metrics.Start();
        var services = new ServiceCollection().AddLogging(builder => builder.AddProvider(logs))
            .AddSingleton<IConfiguration>(config).AddSingleton(Options).AddSingleton<IEventBus>(publisher);
        services.AddTenantEnforcement(config);
        services.AddSingleton<IHttpClientFactory>(new Factory(authority));
        await using var provider = services.BuildServiceProvider();
        var hosted = Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<StaffProjectionRetryWorker>(hosted);
        await hosted.StartAsync(default);
        try
        {
            await publisher.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            publisher.Fail = false;
            await publisher.Published.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Publishing precedes the acknowledgement transaction; wait for its durable completion.
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
                if ((await database.Set<StaffProjectionMessage>().SingleAsync(limit.Token)).PublishedAtUtc is not null) break;
                await Task.Delay(25, limit.Token);
            }
        }
        finally { await hosted.StopAsync(default); }
        var delivered = Assert.Single(publisher.Messages);
        Assert.False(delivered.Active);
        Assert.Equal(organisation, delivered.OrganisationId);
        Assert.Equal(membership, delivered.OrganisationMembershipId);
        Assert.True(authority.Calls >= 2); // A new authoritative decision for each autonomous cycle.
        Assert.Contains("pending_failure", outcomes); Assert.Contains("published", outcomes);
        Assert.Contains(logs.Messages, text => text.Contains("automatic retry"));
        Assert.DoesNotContain(logs.Messages, text => text.Contains("private") || text.Contains(organisation.ToString()));
        await using var otherDatabase = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(other));
        Assert.Null((await otherDatabase.Set<StaffProjectionMessage>().SingleAsync()).PublishedAtUtc);
    }

    [Fact]
    public async Task Denied_worker_leaves_rows_pending_and_recovers_after_live_access_restoration()
    {
        var organisation = Guid.NewGuid(); await Seed(organisation, Guid.NewGuid(), true);
        var publisher = new Publisher { Fail = false }; var authority = new Authority { Allow = false };
        var config = Configuration(organisation);
        var services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(config)
            .AddSingleton(Options).AddSingleton<IEventBus>(publisher);
        services.AddTenantEnforcement(config);
        services.AddSingleton<IHttpClientFactory>(new Factory(authority));
        await using var provider = services.BuildServiceProvider();
        var worker = (StaffProjectionRetryWorker)Assert.Single(provider.GetServices<IHostedService>());
        Assert.True(await worker.DispatchCycleAsync(default));
        Assert.Empty(publisher.Messages);
        authority.Allow = true;
        Assert.False(await worker.DispatchCycleAsync(default));
        Assert.Single(publisher.Messages);
        Assert.Equal(2, authority.Calls);
        authority.Allow = false;
        Assert.True(await worker.DispatchCycleAsync(default)); // no cached worker grant
    }

    [Fact]
    public async Task Batch_is_bounded_and_bad_record_does_not_starve_later_records()
    {
        var organisation = Guid.NewGuid();
        await using (var seed = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation)))
        {
            for (var index = 0; index < 101; index++)
            {
                var message = Message(organisation, Guid.NewGuid(), true);
                seed.Add(new StaffProjectionMessage { EventId = message.Id,
                    Payload = index == 0 ? "invalid-json" : JsonSerializer.Serialize(message) });
            }
            await seed.SaveChangesAsync();
        }
        var publisher = new Publisher { Fail = false };
        var scope = TenantEnforcementTests.Scope(organisation);
        await using var database = new ApplicationDbContext(Options, scope);
        var outbox = new StaffProjectionOutbox(database, scope, publisher,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StaffProjectionOutbox>.Instance, Configuration(organisation));
        await outbox.DispatchAsync(default);
        Assert.Equal(99, publisher.Messages.Count);
        Assert.Equal(2, await database.Set<StaffProjectionMessage>().CountAsync(x => x.PublishedAtUtc == null));
        await outbox.DispatchAsync(default);
        Assert.Equal(100, publisher.Messages.Count);
        Assert.Single(await database.Set<StaffProjectionMessage>().Where(x => x.PublishedAtUtc == null).ToListAsync());
    }

    [Fact]
    public void Staff_mutation_cannot_stage_without_autonomous_retry_coverage()
    {
        var scope = TenantEnforcementTests.Scope(Guid.NewGuid());
        using var database = new ApplicationDbContext(Options, scope);
        var team = new AdminAreaManagement.Core.Entities.Team("Synthetic", "SYN");
        var staff = new AdminAreaManagement.Core.Entities.StaffMember("Synthetic", "Worker", team, "synthetic");
        staff.LinkMembership(Guid.NewGuid());
        var outbox = new StaffProjectionOutbox(database, scope, new Publisher(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StaffProjectionOutbox>.Instance, Configuration(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => outbox.Stage(staff, team));
        Assert.Empty(database.ChangeTracker.Entries<StaffProjectionMessage>());
    }

    [Fact]
    public async Task Missing_worker_configuration_fails_startup_instead_of_silently_disabling_recovery()
    {
        var services = new ServiceCollection().AddLogging();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config).AddTenantEnforcement(config);
        await using var provider = services.BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Assert.Single(provider.GetServices<IHostedService>()).StartAsync(default));
    }

    private async Task Seed(Guid organisation, Guid membership, bool active)
    {
        await using var database = new ApplicationDbContext(Options, TenantEnforcementTests.Scope(organisation));
        var message = Message(organisation, membership, active);
        database.Add(new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) });
        await database.SaveChangesAsync();
    }
    private static StaffProjectionChangedV1 Message(Guid organisation, Guid membership, bool active)
        => new(organisation, membership, 2, active, "private-first", "private-last", "private-user", "private-team", "T");
    private static IConfiguration Configuration(Guid organisation) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["TenantWorker:SubjectId"] = "worker",
            ["TenantWorker:BearerToken"] = "synthetic-test-token",
            ["TenantWorker:OrganisationIds:0"] = organisation.ToString(),
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/" }).Build();
    private sealed class Publisher : IEventBus
    {
        public volatile bool Fail = true;
        public ConcurrentBag<StaffProjectionChangedV1> Messages { get; } = new();
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task PublishAsync(Event message)
        {
            if (Fail) { Failed.TrySetResult(); return Task.FromException(new HttpRequestException("private-broker-detail")); }
            Messages.Add((StaffProjectionChangedV1)message); Published.TrySetResult(); return Task.CompletedTask;
        }
    }
    private sealed class Factory(Authority authority) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(authority, disposeHandler: false); }
    private sealed class Authority : HttpMessageHandler
    {
        public bool Allow { get; set; } = true;
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("synthetic-test-token", request.Headers.Authorization?.Parameter);
            return Task.FromResult(Allow ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { ContractVersion = 1, SubjectId = "worker",
                    OrganisationId = Guid.Parse(request.RequestUri!.Segments.Last()),
                    OrganisationMembershipId = Guid.NewGuid(), EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                    DecisionVersion = "1", ObservedAtUtc = DateTimeOffset.UtcNow })
            } : new(HttpStatusCode.Forbidden));
        }
    }
    private sealed class Logs : ILoggerProvider
    {
        public ConcurrentBag<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentBag<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, error));
        }
    }
}
