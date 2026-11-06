using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using ClientManagement.API.Services;
using ClientManagement.Application;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

// These host probes own the absence of process-global Activity listeners.
[CollectionDefinition("Client authority production host", DisableParallelization = true)]
public sealed class ClientAuthorityProductionHostCollection { }

[Collection("Client authority production host")]
[Trait("Issue", "ZEK-17")]
public sealed class AuthorityCorrectionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Post_200_body_io_is_a_named_dependency_failure(bool httpIo)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenBody(httpIo)) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://auth.invalid/") };
        var timer = Stopwatch.StartNew();
        var result = await new CurrentTenantAccessClient(http, new Credential()).ResolveAsync("private-subject", Guid.NewGuid(), default);
        Assert.Equal(TenantAccessOutcome.Unavailable, result.Outcome);
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, result.Reason);
        Assert.Null(result.Membership);
        Assert.InRange(handler.Calls, 1, 2);
        Assert.InRange(timer.Elapsed.TotalSeconds, 0, 2.5);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Post_200_body_io_reaches_real_middleware_503_without_continuation_or_context(bool httpIo)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenBody(httpIo)) });
        using var http = new HttpClient(handler) { BaseAddress = new("https://auth.invalid/") };
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(http, new Credential()), new Identity(Guid.NewGuid()), scope, scope);
        var continued = false;
        var context = new DefaultHttpContext();
        await new TenantAccessMiddleware(async _ =>
        {
            await operation.AuthorizeAsync(new("ReferenceData.View"), default);
            continued = true;
        }).InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.False(continued);
        Assert.ThrowsAny<InvalidOperationException>(() => scope.Current);
        Assert.InRange(handler.Calls, 1, 2);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task Integrity_event_uses_normal_production_host_logging_without_activity_listener(bool operationPath, bool throwLogger)
    {
        var organisation = Guid.NewGuid();
        var firstMembership = Guid.NewGuid();
        var secondMembership = Guid.NewGuid();
        using var records = new RecordingProvider(throwLogger);
        var handler = new Handler(call => Response(organisation, operationPath ? "private-subject" : "private-other-subject",
            call == 1 ? firstMembership : secondMembership));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/" }).Build();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(records))
            .ConfigureServices(services =>
            {
                services.AddApplication();
                services.AddTenantEnforcement(configuration);
                services.AddHttpClient<ICurrentTenantAccess, CurrentTenantAccessClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddScoped<IOperationIdentity>(_ => new Identity(organisation));
                services.AddScoped<ITenantAccessCredential>(_ => new Credential());
            }).Build();
        var eventSource = Assert.IsType<System.Diagnostics.ActivitySource>(typeof(TenantAccessDiagnostics)
            .GetField("Events", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
        Assert.False(eventSource.HasListeners());
        var resolveSource = Assert.IsType<System.Diagnostics.ActivitySource>(typeof(CurrentTenantAccessClient)
            .GetField("Traces", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
        Assert.False(resolveSource.HasListeners());
        using var scope = host.Services.CreateScope();
        var operation = scope.ServiceProvider.GetRequiredService<TenantOperation>();
        if (operationPath) await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new("ReferenceData.View"), default));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, error.Reason);
        if (throwLogger)
        {
            Assert.Empty(records.Entries.Where(entry => entry.Event.Name == TenantAccessDiagnostics.IntegrityMismatch));
            Assert.ThrowsAny<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current);
            var continued = false;
            var context = new DefaultHttpContext();
            await new TenantAccessMiddleware(async _ =>
            {
                await operation.AuthorizeAsync(new("ReferenceData.View"), default);
                continued = true;
            }).InvokeAsync(context);
            Assert.Equal(503, context.Response.StatusCode);
            Assert.False(continued);
            Assert.ThrowsAny<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current);
            return;
        }
        var emitted = Assert.Single(records.Entries.Where(entry => entry.Event.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal(LogLevel.Warning, emitted.Level);
        Assert.Equal(operationPath ? typeof(TenantOperation).FullName : typeof(CurrentTenantAccessClient).FullName, emitted.Category);
        Assert.Null(emitted.Exception);
        Assert.All(emitted.Fields, field => Assert.Contains(field.Key,
            new[] { "event_code", "service", "mismatch_class", "contract_version", "observed_at_utc", "{OriginalFormat}" }));
        Assert.Equal(6, emitted.Fields.Count);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, emitted.Fields["event_code"]);
        Assert.Equal("ClientManagement", emitted.Fields["service"]);
        Assert.Equal(operationPath ? "contradictory_response" : "subject_mismatch", emitted.Fields["mismatch_class"]);
        Assert.Equal(1, emitted.Fields["contract_version"]);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(emitted.Fields["observed_at_utc"]).Offset);
        Assert.Contains(TenantAccessDiagnostics.IntegrityMismatch, emitted.Message, StringComparison.Ordinal);
        var serialized = string.Join("|", emitted.Fields.Select(field => field.Value?.ToString()));
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(organisation.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(firstMembership.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondMembership.ToString(), serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReferenceData.View", serialized, StringComparison.Ordinal);
        Assert.InRange(handler.Calls, 1, 2);
    }

    [Fact]
    public async Task Actual_worker_consumer_logs_mismatch_and_refuses_before_any_database_continuation()
    {
        var organisation = Guid.NewGuid(); var membership = Guid.NewGuid();
        var bearer = Guid.NewGuid().ToString("N");
        var contexts = new ConcurrentBag<TenantContextScope>();
        using var records = new RecordingProvider();
        var handler = new Handler(_ => Response(organisation, "private-wrong-worker", membership));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["TenantWorker:SubjectId"] = "private-worker",
            ["TenantWorker:BearerToken"] = bearer
        }).Build();
        using var host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(records))
            .ConfigureServices(services =>
            {
                services.AddApplication();
                services.AddTenantEnforcement(configuration);
                services.AddHttpClient("TenantWorkerAccess").ConfigurePrimaryHttpMessageHandler(() => handler);
                services.AddScoped<TenantContextScope>(_ =>
                {
                    var context = new TenantContextScope();
                    contexts.Add(context);
                    return context;
                });
            }).Build();
        var eventSource = Assert.IsType<ActivitySource>(typeof(TenantAccessDiagnostics)
            .GetField("Events", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null));
        Assert.False(eventSource.HasListeners());
        using (var checkScope = host.Services.CreateScope())
            Assert.Null(checkScope.ServiceProvider.GetService<ClientManagement.Infrastructure.Persistence.ApplicationDbContext>());
        var consumer = new ClientManagement.Infrastructure.Messaging.StaffProjectionConsumer(
            host.Services.GetRequiredService<IServiceScopeFactory>(), configuration);
        var message = new Zeka.Contracts.Staff.V1.StaffProjectionChangedV1(organisation, membership, 1, true,
            "Synthetic", "Worker", "synthetic.worker", "Synthetic", "SYN");
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(message));
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, error.Reason);
        Assert.Equal(1, handler.Calls);
        Assert.NotEmpty(contexts);
        Assert.All(contexts, context => Assert.ThrowsAny<InvalidOperationException>(() => context.Current));
        var logged = Assert.Single(records.Entries.Where(entry => entry.Event.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal(typeof(CurrentTenantAccessClient).FullName, logged.Category);
        Assert.Equal("subject_mismatch", logged.Fields["mismatch_class"]);
        Assert.All(logged.Fields, field => Assert.Contains(field.Key,
            new[] { "event_code", "service", "mismatch_class", "contract_version", "observed_at_utc", "{OriginalFormat}" }));
        var emitted = string.Join("|", logged.Fields.Select(field => field.Value?.ToString()));
        Assert.DoesNotContain("private", emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(bearer, emitted, StringComparison.Ordinal);
        Assert.DoesNotContain(organisation.ToString(), emitted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(membership.ToString(), emitted, StringComparison.OrdinalIgnoreCase);
    }
    private static HttpResponseMessage Response(Guid organisation, string subject, Guid membership) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new
        {
            ContractVersion = 1, SubjectId = subject, OrganisationId = organisation, OrganisationMembershipId = membership,
            EffectivePermissionCodes = new[] { "ReferenceData.View" }, DecisionVersion = "1", ObservedAtUtc = DateTimeOffset.UtcNow
        })
    };
    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity { public string SubjectId => "private-subject"; }
    private sealed record Credential : ITenantAccessCredential { public string BearerToken { get; } = Guid.NewGuid().ToString("N"); }
    private sealed class Handler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(++Calls));
    }
    private sealed class BrokenBody(bool httpIo) : Stream
    {
        private Exception Failure => httpIo ? new HttpIOException(HttpRequestError.ResponseEnded, "private-body-transport-detail")
            : new IOException("private-body-transport-detail");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw Failure;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(Failure);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed record LogEntry(string Category, LogLevel Level, EventId Event, Dictionary<string, object?> Fields,
        string Message, Exception? Exception);
    private sealed class RecordingProvider(bool throwIntegrity = false) : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries, throwIntegrity);
        public void Dispose() { }
    }
    private sealed class RecordingLogger(string category, ConcurrentBag<LogEntry> entries, bool throwIntegrity) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (throwIntegrity && eventId.Name == TenantAccessDiagnostics.IntegrityMismatch) throw new InvalidOperationException("private-logger-error");
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(category, logLevel, eventId, fields, formatter(state, exception), exception));
        }
    }
}
[Collection("Client authority production host")]
[Trait("Issue", "ZEK-17")]
public sealed class AuthorityOperationalChannelTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Logging_and_activity_operational_channels_fail_independently(bool throwLogger)
    {
        using var invocation = new Activity("test.operational.channels").Start();
        var events = new ConcurrentBag<ActivityEvent>();
        var logger = new ChannelLogger(throwLogger);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CurrentTenantAccessClient.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != invocation.TraceId || activity.OperationName != "tenant_access.diagnostic") return;
                if (!throwLogger) throw new InvalidOperationException("private-activity-exporter-error");
                foreach (var item in activity.Events) events.Add(item);
            }
        };
        ActivitySource.AddActivityListener(listener);
        TenantAccessDiagnostics.Integrity("subject_mismatch", logger);
        Assert.Equal(1, logger.Calls);
        if (throwLogger) Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, Assert.Single(events).Name);
        else Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, Assert.Single(logger.Recorded).Name);
    }
    private sealed class ChannelLogger(bool shouldThrow) : ILogger
    {
        public int Calls { get; private set; }
        public List<EventId> Recorded { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Calls++;
            if (shouldThrow) throw new InvalidOperationException("private-logger-error");
            Recorded.Add(eventId);
        }
    }
}