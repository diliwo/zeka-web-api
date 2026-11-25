using System.Data.Common;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Infrastructure.IntegrationTests;

public sealed class VerificationMailWorkerBoundaryTests
{
    [Fact]
    public async Task Precancelled_start_never_creates_a_scope_or_logs_a_provider_failure()
    {
        var scopes = new ObservedScopes(null);
        var logger = new SafeLogger();
        using var worker = new VerificationMailWorker(scopes, logger);
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.StartAsync(stopping.Token));
        Assert.Equal(0, scopes.Created);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task Empty_queue_disposes_its_scope_and_stops_the_current_batch()
    {
        await using var store = await RegistrationStore.CreateAsync();
        using var input = new RegistrationOptionInputs();
        using var options = input.Read();
        using var stopping = new CancellationTokenSource();
        var scopes = new ObservedScopes(new(store.Database, new(options), new RefuseDelivery(), new RegistrationStore.Clock()));
        scopes.AfterDispose = count => { if (count == 1) stopping.Cancel(); };
        var logger = new SafeLogger();
        using var worker = new VerificationMailWorker(scopes, logger);
        await worker.StartAsync(stopping.Token);
        await scopes.FirstDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, scopes.AsyncDisposed);
        Assert.Equal(0, scopes.SynchronouslyDisposed);
        Assert.Empty(logger.Messages);
    }

    [Fact]
    public async Task A_tick_never_creates_a_ninth_dispatch_scope_even_when_work_remains()
    {
        await using var store = await RegistrationStore.CreateAsync();
        var user = RegistrationStore.CreateUser();
        store.Database.Users.Add(user);
        for (var generation = 1; generation <= 9; generation++)
        {
            var challenge = new EmailVerificationChallenge
            {
                Id = Guid.NewGuid(), UserId = user.Id, Generation = generation, KeyId = "active",
                AddressBinding = "unit-address-binding", StampBinding = "unit-stamp-binding", TokenHash = "unit-hash",
                CreatedAtUtc = RegistrationStore.Now, ExpiresAtUtc = RegistrationStore.Now.AddDays(1)
            };
            challenge.Transition(VerificationChallengeState.Superseded, RegistrationStore.Now, Guid.NewGuid());
            store.Database.EmailVerificationChallenges.Add(challenge);
            store.Database.VerificationMailOutboxMessages.Add(new()
            {
                Id = Guid.NewGuid(), OperationId = Guid.NewGuid(), ChallengeId = challenge.Id, Generation = generation,
                PayloadVersion = VerificationMailOutboxMessage.SupportedVersion + 1,
                EnqueuedAtUtc = RegistrationStore.Now, NextAttemptAtUtc = RegistrationStore.Now, Version = Guid.NewGuid()
            });
        }
        await store.Database.SaveChangesAsync();
        using var input = new RegistrationOptionInputs();
        using var options = input.Read();
        using var stopping = new CancellationTokenSource();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopes = new ObservedScopes(new(store.Database, new(options), new RefuseDelivery(), new RegistrationStore.Clock()));
        scopes.AfterDispose = count =>
        {
            if (count == 8) { stopping.Cancel(); finished.TrySetResult(); }
        };
        using var worker = new VerificationMailWorker(scopes, new SafeLogger());
        await worker.StartAsync(stopping.Token);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(8, scopes.Created);
        Assert.Equal(8, scopes.AsyncDisposed);
        Assert.Equal(8, await store.Database.VerificationMailOutboxMessages.CountAsync(row => row.Status == VerificationMailState.DeadLettered));
        Assert.Equal(1, await store.Database.VerificationMailOutboxMessages.CountAsync(row => row.Status == VerificationMailState.Pending));
    }

    [Fact]
    public async Task Provider_resolution_failure_is_logged_only_as_a_fixed_safe_warning()
    {
        var privateMarker = Guid.NewGuid().ToString("N");
        using var stopping = new CancellationTokenSource();
        var scopes = new ObservedScopes(null) { ResolutionFailure = new IOException(privateMarker) };
        var logger = new SafeLogger { AfterLog = stopping.Cancel };
        using var worker = new VerificationMailWorker(scopes, logger);
        await worker.StartAsync(stopping.Token);
        await logger.Logged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        var message = Assert.Single(logger.Messages);
        Assert.Equal(LogLevel.Warning, message.Level);
        Assert.Null(message.Error);
        Assert.Equal("Verification mail dispatch did not complete; the lease controls recovery.", message.Text);
        Assert.DoesNotContain(privateMarker, message.Text, StringComparison.Ordinal);
        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, scopes.AsyncDisposed);
    }

    [Fact]
    public async Task Host_stop_cancels_the_real_dispatch_query_and_disposes_without_warning()
    {
        var boundary = new CancelledQuery();
        await using var store = await RegistrationStore.CreateAsync(boundary);
        using var input = new RegistrationOptionInputs();
        using var options = input.Read();
        var scopes = new ObservedScopes(new(store.Database, new(options), new RefuseDelivery(), new RegistrationStore.Clock()));
        var logger = new SafeLogger();
        using var worker = new VerificationMailWorker(scopes, logger);
        boundary.Armed = true;
        await worker.StartAsync(CancellationToken.None);
        var token = await boundary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(token.CanBeCanceled);
        await worker.StopAsync(CancellationToken.None);
        Assert.True(token.IsCancellationRequested);
        Assert.True(worker.ExecuteTask!.IsCompleted);
        Assert.Equal(1, scopes.Created);
        Assert.Equal(1, scopes.AsyncDisposed);
        Assert.Empty(logger.Messages);
    }

    private sealed class CancelledQuery : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource<CancellationToken> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            Entered.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("CANCELLED_UNIT_QUERY_CANNOT_COMPLETE");
        }
    }

    private sealed class RefuseDelivery : IVerificationMailDelivery
    {
        public Task SendAsync(string recipient, string verificationToken, CancellationToken cancellationToken)
            => throw new InvalidOperationException("SMTP_IS_NOT_PART_OF_THE_WORKER_UNIT_FIXTURE");
    }

    // Only scope lifecycle is simulated. Dispatching always uses the real production dispatcher.
    private sealed class ObservedScopes(VerificationMailDispatcher? dispatcher) : IServiceScopeFactory
    {
        private VerificationMailDispatcher? Dispatcher { get; } = dispatcher;
        public int Created { get; private set; }
        public int AsyncDisposed { get; private set; }
        public int SynchronouslyDisposed { get; private set; }
        public Exception? ResolutionFailure { get; init; }
        public Action<int>? AfterDispose { get; set; }
        public TaskCompletionSource FirstDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IServiceScope CreateScope() { Created++; return new Scope(this); }
        private sealed class Scope(ObservedScopes owner) : IServiceScope, IAsyncDisposable, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType)
            {
                if (owner.ResolutionFailure is { } error) throw error;
                return serviceType == typeof(VerificationMailDispatcher) ? owner.Dispatcher : null;
            }
            public void Dispose() => owner.SynchronouslyDisposed++;
            public ValueTask DisposeAsync()
            {
                owner.AsyncDisposed++;
                owner.AfterDispose?.Invoke(owner.AsyncDisposed);
                owner.FirstDisposed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
    private sealed class SafeLogger : ILogger<VerificationMailWorker>
    {
        public List<(LogLevel Level, string Text, Exception? Error)> Messages { get; } = [];
        public TaskCompletionSource Logged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? AfterLog { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception), exception));
            AfterLog?.Invoke();
            Logged.TrySetResult();
        }
    }
}
