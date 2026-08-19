using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Infrastructure.Outbox;
using AuthManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests;

public sealed class OnboardingPersistenceFoundationTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Equivalent_completed_retry_returns_original_safe_result()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        await using (var scope = context.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            (await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Zeka\"}",
                TimeSpan.FromMinutes(1), TimeSpan.FromDays(1))).Kind.Should().Be(IdempotencyDecisionKind.Started);
            await store.CompleteAsync(userId, "create-organisation", "key-1", resourceId,
                $"{{\"organisationId\":\"{resourceId}\"}}");
        }

        await using var replayScope = context.Services.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<IIdempotencyStore>()
            .TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Zeka\"}",
                TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

        replay.Kind.Should().Be(IdempotencyDecisionKind.Replay);
        replay.ResourceId.Should().Be(resourceId);
        replay.SafeResponse.Should().Contain(resourceId.ToString());
    }

    [Fact]
    public async Task Reusing_key_for_different_request_returns_conflict()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using var scope = context.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var userId = Guid.NewGuid();
        await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"One\"}",
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

        var decision = await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Two\"}",
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

        decision.Kind.Should().Be(IdempotencyDecisionKind.Conflict);
    }

    [Fact]
    public async Task Expired_processing_claim_can_be_taken_over()
    {
        var clock = new ManualTimeProvider(Now);
        await using var context = await IdentityTestContext.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var userId = Guid.NewGuid();
        await using var scope = context.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Zeka\"}",
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
        clock.UtcNow = clock.UtcNow.AddMinutes(2);

        var result = await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Zeka\"}",
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

        result.Kind.Should().Be(IdempotencyDecisionKind.Started);
    }

    [Fact]
    public async Task Concurrent_claims_produce_one_started_operation()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        var userId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IdempotencyDecisionKind> ClaimAsync()
        {
            await using var scope = context.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
            await gate.Task;
            var result = await store.TryBeginAsync(userId, "create-organisation", "same-key", "{\"name\":\"Zeka\"}",
                TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));
            return result.Kind;
        }

        var claims = new[] { ClaimAsync(), ClaimAsync() };
        gate.SetResult();
        var results = await Task.WhenAll(claims);

        results.Count(value => value == IdempotencyDecisionKind.Started).Should().Be(1);
        results.Count(value => value == IdempotencyDecisionKind.Processing).Should().Be(1);
        await using var verificationScope = context.Services.CreateAsyncScope();
        (await verificationScope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .IdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Audit_supports_platform_and_tenant_scope_with_correlation_id()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using var scope = context.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var organisationId = Guid.NewGuid();

        await writer.WriteAsync(new AuditEntryRequest(Guid.NewGuid(), null, "registration.accepted",
            "User", "user-1", "Succeeded", "corr-platform"));
        await writer.WriteAsync(new AuditEntryRequest(Guid.NewGuid(), organisationId, "organisation.created",
            "Organisation", organisationId.ToString(), "Succeeded", "corr-tenant",
            new Dictionary<string, string> { ["agreementVersion"] = "1" }));

        var entries = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().AuditEntries.ToListAsync();
        entries.Should().HaveCount(2);
        entries.Should().Contain(value => value.OrganisationId == null && value.CorrelationId == "corr-platform");
        entries.Should().Contain(value => value.OrganisationId == organisationId && value.CorrelationId == "corr-tenant");
        entries.Should().OnlyContain(value => value.OccurredAtUtc == Now);
    }

    [Fact]
    public async Task Audit_outbox_and_business_state_roll_back_together()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using (var scope = context.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
            var outbox = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
            await audit.WriteAsync(new AuditEntryRequest(Guid.NewGuid(), null, "test", "Test", "1", "Succeeded", "corr"));
            await outbox.EnqueueAsync(new OutboxMessageRequest("test.created", 1, "{\"id\":\"1\"}", Now, "corr"));
            await transaction.RollbackAsync();
        }

        await using var verificationScope = context.Services.CreateAsyncScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await verificationDb.AuditEntries.CountAsync()).Should().Be(0);
        (await verificationDb.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Dispatcher_retries_then_processes_transient_failure()
    {
        var clock = new ManualTimeProvider(Now);
        var publisher = new RecordingPublisher(failuresBeforeSuccess: 1);
        await using var context = await CreateDispatcherContextAsync(clock, publisher, maxAttempts: 3);
        Guid messageId;
        await using (var scope = context.Services.CreateAsyncScope())
            messageId = await scope.ServiceProvider.GetRequiredService<IOutboxWriter>()
                .EnqueueAsync(new OutboxMessageRequest("organisation.created", 1, "{\"id\":\"1\"}", Now, "corr"));

        await DispatchAsync(context);
        clock.UtcNow = clock.UtcNow.AddSeconds(11);
        await DispatchAsync(context);

        await using var verificationScope = context.Services.CreateAsyncScope();
        var message = await verificationScope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .OutboxMessages.SingleAsync(value => value.Id == messageId);
        message.AttemptCount.Should().Be(2);
        message.ProcessedAtUtc.Should().Be(clock.UtcNow);
        message.DeadLetteredAtUtc.Should().BeNull();
        publisher.Published.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dispatcher_dead_letters_poison_message_and_surfaces_safe_error()
    {
        var clock = new ManualTimeProvider(Now);
        var publisher = new RecordingPublisher(failuresBeforeSuccess: int.MaxValue);
        await using var context = await CreateDispatcherContextAsync(clock, publisher, maxAttempts: 2);
        await using (var scope = context.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IOutboxWriter>()
                .EnqueueAsync(new OutboxMessageRequest("organisation.created", 1, "{\"id\":\"1\"}", Now, "corr"));

        await DispatchAsync(context);
        clock.UtcNow = clock.UtcNow.AddSeconds(11);
        await DispatchAsync(context);

        await using var verificationScope = context.Services.CreateAsyncScope();
        var message = await verificationScope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .OutboxMessages.SingleAsync();
        message.AttemptCount.Should().Be(2);
        message.DeadLetteredAtUtc.Should().Be(clock.UtcNow);
        message.LastError.Should().Be(nameof(InvalidOperationException));
        message.LastError.Should().NotContain("raw-secret");
    }

    [Theory]
    [InlineData("{\"password\":\"value\"}")]
    [InlineData("{\"refreshToken\":\"value\"}")]
    [InlineData("{\"clientSecret\":\"value\"}")]
    public async Task Persisted_payloads_reject_credential_fields(string payload)
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using var scope = context.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        var action = () => writer.EnqueueAsync(new OutboxMessageRequest("unsafe", 1, payload, Now, "corr"));

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Idempotency_response_rejects_credential_fields()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using var scope = context.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdempotencyStore>();
        var userId = Guid.NewGuid();
        await store.TryBeginAsync(userId, "create-organisation", "key-1", "{\"name\":\"Zeka\"}",
            TimeSpan.FromMinutes(1), TimeSpan.FromDays(1));

        var action = () => store.CompleteAsync(userId, "create-organisation", "key-1", Guid.NewGuid(),
            "{\"accessToken\":\"unsafe\"}");

        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Audit_metadata_rejects_credential_fields()
    {
        await using var context = await IdentityTestContext.CreateAsync(Now);
        await using var scope = context.Services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var request = new AuditEntryRequest(Guid.NewGuid(), null, "registration.accepted", "User", "1",
            "Succeeded", "corr", new Dictionary<string, string> { ["passwordHash"] = "unsafe" });

        var action = () => writer.WriteAsync(request);

        await action.Should().ThrowAsync<ArgumentException>();
    }

    private static async Task<IdentityTestContext> CreateDispatcherContextAsync(
        ManualTimeProvider clock, IOutboxMessagePublisher publisher, int maxAttempts)
    {
        return await IdentityTestContext.CreateAsync(configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton(publisher);
            services.PostConfigure<OutboxDispatcherOptions>(options =>
            {
                options.BatchSize = 10;
                options.MaxAttempts = maxAttempts;
                options.InitialRetryDelay = TimeSpan.FromSeconds(10);
                options.MaxRetryDelay = TimeSpan.FromMinutes(1);
                options.LeaseDuration = TimeSpan.FromMinutes(1);
            });
        });
    }

    private static async Task DispatchAsync(IdentityTestContext context)
    {
        await using var scope = context.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>().DispatchBatchAsync();
    }

    private sealed class RecordingPublisher(int failuresBeforeSuccess) : IOutboxMessagePublisher
    {
        private int _remainingFailures = failuresBeforeSuccess;
        public List<OutboxMessageEnvelope> Published { get; } = [];

        public Task PublishAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
        {
            if (_remainingFailures-- > 0)
                throw new InvalidOperationException("raw-secret must never be persisted");
            Published.Add(message);
            return Task.CompletedTask;
        }
    }
}
