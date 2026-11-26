using System.Net.Mail;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Infrastructure.IntegrationTests;

public sealed class VerificationMailDispatcherBoundaryTests
{
    [Fact]
    public async Task Empty_queue_has_no_delivery_or_write()
    {
        await using var fixture = await DispatchInputs.CreateAsync("empty");
        Assert.False(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Delivery.Calls);
        Assert.Empty(await fixture.Store.Database.VerificationMailOutboxMessages.ToListAsync());
    }

    [Theory]
    [InlineData("future-pending")]
    [InlineData("future-retry")]
    [InlineData("live-lease")]
    [InlineData("delivered")]
    public async Task Ineligible_messages_are_not_leased_or_delivered(string state)
    {
        await using var fixture = await DispatchInputs.CreateAsync(state);
        var version = fixture.Message!.Version;
        Assert.False(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        await fixture.Store.Database.Entry(fixture.Message).ReloadAsync();
        Assert.Equal(version, fixture.Message.Version);
        Assert.Equal(0, fixture.Message.Attempts);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Theory]
    [InlineData("payload", VerificationMailState.DeadLettered, "UNSUPPORTED_PAYLOAD")]
    [InlineData("generation", VerificationMailState.DeadLettered, "INVALID_CHALLENGE_LINK")]
    [InlineData("superseded", VerificationMailState.Superseded, "SUPERSEDED")]
    [InlineData("consumed", VerificationMailState.Consumed, "CONSUMED")]
    [InlineData("revoked", VerificationMailState.Revoked, "BINDING_REVOKED")]
    [InlineData("active-user", VerificationMailState.Revoked, "BINDING_REVOKED")]
    [InlineData("confirmed-user", VerificationMailState.Revoked, "BINDING_REVOKED")]
    [InlineData("address-changed", VerificationMailState.Revoked, "BINDING_REVOKED")]
    [InlineData("stamp-changed", VerificationMailState.Revoked, "BINDING_REVOKED")]
    [InlineData("expired", VerificationMailState.Expired, "EXPIRED")]
    [InlineData("recipient-missing", VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING")]
    [InlineData("recipient-malformed", VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING")]
    [InlineData("recipient-display-name", VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING")]
    [InlineData("retired-key", VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING")]
    [InlineData("hash-changed", VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING")]
    public async Task Invalid_or_obsolete_challenges_finish_without_submitting_mail(string invalid,
        VerificationMailState expected, string reason)
    {
        await using var fixture = await DispatchInputs.CreateAsync(invalid);
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(expected, message.Status);
        Assert.Equal(reason, message.Outcome);
        Assert.Null(message.LeaseId);
        Assert.Null(message.LeaseExpiresAtUtc);
        Assert.Equal(0, message.Attempts);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Repository_concurrency_refusal_never_continues_to_delivery(int save)
    {
        var boundary = new SaveBoundary { FailAt = save };
        await using var fixture = await DispatchInputs.CreateAsync(boundary: boundary);
        boundary.Armed = true;
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Delivery.Calls);
        Assert.Empty(fixture.Store.Database.ChangeTracker.Entries());
        var message = await fixture.ReadAsync();
        Assert.Equal(save == 1 ? VerificationMailState.Pending : VerificationMailState.Leased, message.Status);
        Assert.Equal(0, message.Attempts);
        Assert.Null(message.DeliveredAtUtc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Expired_lease_after_either_save_stops_before_submission(int save)
    {
        var boundary = new SaveBoundary();
        await using var fixture = await DispatchInputs.CreateAsync(boundary: boundary);
        boundary.AfterSave = count =>
        {
            if (count == save) fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(46);
            return Task.CompletedTask;
        };
        boundary.Armed = true;
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        Assert.Equal(0, fixture.Delivery.Calls);
        Assert.Equal(VerificationMailState.Leased, (await fixture.ReadAsync()).Status);
    }

    [Fact]
    public async Task Authority_change_during_lease_renewal_is_rechecked_before_mail()
    {
        var boundary = new SaveBoundary();
        await using var fixture = await DispatchInputs.CreateAsync(boundary: boundary);
        boundary.AfterSave = async count =>
        {
            if (count != 2) return;
            boundary.Armed = false;
            fixture.Challenge!.Transition(VerificationChallengeState.Superseded, fixture.Clock.UtcNow, Guid.NewGuid());
            await fixture.Store.Database.SaveChangesAsync();
            boundary.Armed = true;
        };
        boundary.Armed = true;
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(VerificationMailState.Superseded, message.Status);
        Assert.Equal("SUPERSEDED", message.Outcome);
        Assert.Equal(1, message.Attempts);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task Exhausted_attempts_dead_letter_without_another_submission()
    {
        await using var fixture = await DispatchInputs.CreateAsync("exhausted");
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(VerificationMailState.DeadLettered, message.Status);
        Assert.Equal("ATTEMPTS_EXHAUSTED", message.Outcome);
        Assert.Equal(4, message.Attempts);
        Assert.Equal(0, fixture.Delivery.Calls);
    }

    [Theory]
    [InlineData("smtp", 0)]
    [InlineData("io", 0)]
    [InlineData("deadline", 0)]
    [InlineData("smtp", 3)]
    public async Task Uncertain_delivery_retries_boundedly_without_recording_acceptance(string failure, int attempts)
    {
        await using var fixture = await DispatchInputs.CreateAsync();
        fixture.Message!.Attempts = attempts;
        await fixture.Store.Database.SaveChangesAsync();
        fixture.Delivery.OnSend = _ => throw failure switch
        {
            "smtp" => new SmtpException("synthetic-transport-refusal"),
            "io" => new IOException("synthetic-transport-loss"),
            _ => new OperationCanceledException()
        };
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(attempts == 3 ? VerificationMailState.DeadLettered : VerificationMailState.RetryScheduled, message.Status);
        Assert.Equal("SMTP_OUTCOME_UNCONFIRMED", message.Outcome);
        Assert.Equal(attempts + 1, message.Attempts);
        Assert.Null(message.DeliveredAtUtc);
        Assert.Null(message.LeaseId);
        if (attempts == 0) Assert.Equal(fixture.Clock.UtcNow.AddSeconds(10), message.NextAttemptAtUtc);
        Assert.Equal(1, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task Host_cancellation_leaves_the_lease_recoverable_without_false_delivery()
    {
        await using var fixture = await DispatchInputs.CreateAsync();
        using var stopping = new CancellationTokenSource();
        fixture.Delivery.OnSend = token =>
        {
            Assert.True(token.CanBeCanceled);
            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        };
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(stopping.Token));
        var message = await fixture.ReadAsync();
        Assert.Equal(VerificationMailState.Leased, message.Status);
        Assert.NotNull(message.LeaseId);
        Assert.Null(message.DeliveredAtUtc);
        Assert.Null(message.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_submission_records_acceptance_and_whether_the_challenge_became_obsolete(bool obsolete)
    {
        await using var fixture = await DispatchInputs.CreateAsync("expired-lease");
        fixture.Delivery.OnSend = async token =>
        {
            Assert.True(token.CanBeCanceled);
            if (obsolete)
            {
                fixture.Challenge!.Transition(VerificationChallengeState.Superseded, fixture.Clock.UtcNow, Guid.NewGuid());
                await fixture.Store.Database.SaveChangesAsync();
            }
        };
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(VerificationMailState.Delivered, message.Status);
        Assert.Equal("SMTP_ACCEPTED", message.Outcome);
        Assert.Equal(fixture.Clock.UtcNow, message.DeliveredAtUtc);
        Assert.Equal(obsolete, message.ObsoleteAtAcceptance);
        Assert.Equal(1, message.Attempts);
        Assert.Equal(1, fixture.Delivery.Calls);
    }

    [Fact]
    public async Task Acceptance_after_lease_expiry_cannot_claim_a_completed_delivery()
    {
        await using var fixture = await DispatchInputs.CreateAsync();
        fixture.Delivery.OnSend = _ => { fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(46); return Task.CompletedTask; };
        Assert.True(await fixture.Dispatcher.DispatchOneAsync(CancellationToken.None));
        var message = await fixture.ReadAsync();
        Assert.Equal(VerificationMailState.Leased, message.Status);
        Assert.Null(message.DeliveredAtUtc);
        Assert.Equal(1, fixture.Delivery.Calls);
    }

    private sealed class SaveBoundary : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int FailAt { get; init; }
        public int Saves { get; private set; }
        public Func<int, Task>? AfterSave { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && ++Saves == FailAt) throw new DbUpdateConcurrencyException("synthetic-unit-adapter-conflict");
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Armed && AfterSave is not null) await AfterSave(Saves);
            return result;
        }
    }

    private sealed class RecordingDelivery : IVerificationMailDelivery
    {
        public int Calls { get; private set; }
        public Func<CancellationToken, Task>? OnSend { get; set; }
        public async Task SendAsync(string recipient, string verificationToken, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.Equal("unit@example.invalid", recipient);
            Assert.True(VerificationTokenService.TryRead(verificationToken, out _));
            if (OnSend is not null) await OnSend(cancellationToken);
        }
    }

    private sealed class DispatchInputs : IAsyncDisposable
    {
        private readonly RegistrationOptionInputs inputs = new();
        private readonly RegistrationOptions options;
        public RegistrationStore Store { get; }
        public RegistrationStore.Clock Clock { get; } = new();
        public RecordingDelivery Delivery { get; } = new();
        public VerificationMailDispatcher Dispatcher { get; }
        public EmailVerificationChallenge? Challenge { get; private set; }
        public VerificationMailOutboxMessage? Message { get; private set; }
        private DispatchInputs(RegistrationStore store)
        {
            Store = store;
            options = inputs.Read();
            Dispatcher = new(store.Database, new VerificationTokenService(options), Delivery, Clock);
        }
        public static async Task<DispatchInputs> CreateAsync(string scenario = "valid", SaveBoundary? boundary = null)
        {
            var store = await RegistrationStore.CreateAsync(boundary is null ? [] : [boundary]);
            var fixture = new DispatchInputs(store);
            if (scenario == "empty") return fixture;
            var user = RegistrationStore.CreateUser();
            var challenge = RegistrationStore.CreateChallenge(user,
                scenario == "expired" ? RegistrationStore.Now : null,
                scenario == "retired-key" ? "retired" : "active");
            var tokens = new VerificationTokenService(fixture.options);
            challenge.TokenHash = VerificationTokenService.TokenHash(tokens.Create(challenge) ?? "retired-unit-key");
            if (scenario == "hash-changed") challenge.TokenHash = Guid.NewGuid().ToString("N");
            if (scenario is "superseded" or "consumed" or "revoked")
                challenge.Transition(scenario == "superseded" ? VerificationChallengeState.Superseded
                    : scenario == "consumed" ? VerificationChallengeState.Consumed : VerificationChallengeState.Revoked,
                    RegistrationStore.Now, Guid.NewGuid());
            if (scenario == "active-user") { user.EmailConfirmed = true; user.ActivateConfirmedIdentity(RegistrationStore.Now); }
            if (scenario == "confirmed-user") user.EmailConfirmed = true;
            if (scenario == "address-changed") user.NormalizedEmail = "OTHER@EXAMPLE.INVALID";
            if (scenario == "stamp-changed") user.SecurityStamp = Guid.NewGuid().ToString("N");
            if (scenario == "recipient-missing") user.Email = null;
            if (scenario == "recipient-malformed") user.Email = "not-an-email";
            if (scenario == "recipient-display-name") user.Email = "Unit <unit@example.invalid>";
            var message = new VerificationMailOutboxMessage
            {
                Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), ChallengeId = challenge.Id,
                Generation = scenario == "generation" ? 2 : 1,
                PayloadVersion = scenario == "payload" ? 2 : VerificationMailOutboxMessage.SupportedVersion,
                EnqueuedAtUtc = RegistrationStore.Now, NextAttemptAtUtc = RegistrationStore.Now,
                Version = Guid.NewGuid(), Attempts = scenario == "exhausted" ? 4 : 0
            };
            if (scenario is "future-pending" or "future-retry") message.NextAttemptAtUtc = RegistrationStore.Now.AddSeconds(1);
            if (scenario == "future-retry") message.Status = VerificationMailState.RetryScheduled;
            if (scenario is "live-lease" or "expired-lease")
            {
                message.Status = VerificationMailState.Leased;
                message.LeaseId = Guid.NewGuid();
                message.LeaseExpiresAtUtc = scenario == "live-lease" ? RegistrationStore.Now.AddMinutes(1) : RegistrationStore.Now;
            }
            if (scenario == "delivered") message.Status = VerificationMailState.Delivered;
            store.Database.AddRange(user, challenge, message);
            await store.Database.SaveChangesAsync();
            fixture.Challenge = challenge;
            fixture.Message = message;
            return fixture;
        }
        public async Task<VerificationMailOutboxMessage> ReadAsync()
        {
            Store.Database.ChangeTracker.Clear();
            return await Store.Database.VerificationMailOutboxMessages.SingleAsync();
        }
        public async ValueTask DisposeAsync()
        { options.Dispose(); inputs.Dispose(); await Store.DisposeAsync(); }
    }
}
