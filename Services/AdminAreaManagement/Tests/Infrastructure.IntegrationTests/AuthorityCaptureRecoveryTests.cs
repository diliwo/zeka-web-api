using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AdminAreaManagement.Application;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Authorization;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.WorkerCredentials;
using Zeka.WorkerCredentials.Testing;

namespace Infrastructure.IntegrationTests;

[CollectionDefinition("AdminArea normal-host operational route", DisableParallelization = true)]
public sealed class AdminAreaOperationalRouteCollection { }

[Collection("AdminArea normal-host operational route")]
[Trait("Issue", "17")]
public sealed class AuthorityCaptureRecoveryTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Body_read_failures_after_success_headers_are_named_503_without_context_or_continuation(bool httpIo, bool throughMiddleware)
    {
        var org = Guid.NewGuid(); var reads = 0; var calls = 0;
        var privateMessage = Guid.NewGuid().ToString("N");
        using var http = new HttpClient(new Handler(_ =>
        {
            calls++;
            return new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(() => reads++,
                httpIo ? new HttpIOException(HttpRequestError.ResponseEnded, privateMessage) : new IOException(privateMessage))) };
        })) { BaseAddress = new("https://auth.invalid/") };
        var adapter = new CurrentTenantAccessClient(http, new Credential());
        var timer = Stopwatch.StartNew();
        if (!throughMiddleware)
        {
            var decision = await adapter.ResolveAsync("subject", org, default);
            Assert.Equal(TenantAccessOutcome.Unavailable, decision.Outcome);
            Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, decision.Reason);
            Assert.Null(decision.Membership);
            Assert.DoesNotContain(privateMessage, decision.Reason ?? "", StringComparison.Ordinal);
        }
        else
        {
            var scope = new TenantContextScope(); var continued = false;
            var op = new TenantOperation(adapter, new Identity("subject", org), scope, scope);
            var context = new DefaultHttpContext();
            await new TenantAccessMiddleware(async _ =>
            {
                await op.AuthorizeAsync(new("ReferenceData.View"), default);
                continued = true;
            }).InvokeAsync(context);
            Assert.Equal(503, context.Response.StatusCode); Assert.False(continued);
            Assert.Throws<TenantContextException>(() => scope.Current);
        }
        Assert.True(reads > 0); Assert.InRange(calls, 1, 2);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)]
    public async Task Normal_host_production_DI_routes_integrity_event_to_logger_without_activity_listener(bool operationReentry, bool throwLogger)
    {
        var records = new Records(throwLogger); var org = Guid.NewGuid(); var membership = Guid.NewGuid(); var calls = 0;
        using var host = new HostBuilder().ConfigureLogging(logs => logs.ClearProviders().AddProvider(records))
            .ConfigureServices((_, services) =>
            {
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/" }).Build();
                services.AddApplication();
                services.AddTenantEnforcement(configuration);
                services.AddScoped<IOperationIdentity>(_ => new Identity("subject", org));
                services.AddScoped<ITenantAccessCredential>(_ => new Credential());
                services.Configure<HttpClientFactoryOptions>(nameof(ICurrentTenantAccess), options =>
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new Handler(_ =>
                    {
                        calls++;
                        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                        {
                            ContractVersion = 1, SubjectId = operationReentry ? "subject" : "private-other-subject",
                            OrganisationId = org, OrganisationMembershipId = calls == 1 ? membership : Guid.NewGuid(),
                            EffectivePermissionCodes = new[] { "ReferenceData.View" }, DecisionVersion = "v1", ObservedAtUtc = DateTimeOffset.UtcNow
                        }) };
                    })));
            }).Build();
        using var scope = host.Services.CreateScope();
        var operation = scope.ServiceProvider.GetRequiredService<TenantOperation>();
        var adapterSource = (ActivitySource)typeof(CurrentTenantAccessClient).GetField("Traces", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var signalSource = (ActivitySource)typeof(TenantAccessDiagnostics).GetField("Signals", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.False(adapterSource.HasListeners()); Assert.False(signalSource.HasListeners());
        if (operationReentry) await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        var context = new DefaultHttpContext(); var continued = false;
        await new TenantAccessMiddleware(async _ =>
        {
            await operation.AuthorizeAsync(new("ReferenceData.View"), default);
            continued = true;
        }).InvokeAsync(context);
        Assert.Equal(503, context.Response.StatusCode); Assert.False(continued);
        if (!operationReentry) Assert.Throws<TenantContextException>(() => scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current);
        Assert.Equal(1, records.IntegrityCalls);
        var record = Assert.Single(records.Items.Where(r => r.Event.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, record.Event.Name);
        Assert.Equal(new[] { "contract_version", "event_code", "mismatch_class", "observed_at_utc", "service", "{OriginalFormat}" }.Order(StringComparer.Ordinal),
            record.State.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(TenantAccessDiagnostics.IntegrityMismatch, record.State["event_code"]);
        Assert.Equal("AdminAreaManagement", record.State["service"]); Assert.Equal(1, record.State["contract_version"]);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(record.State["observed_at_utc"]).Offset);
        Assert.Equal(operationReentry ? "contradictory_response" : "subject_mismatch", record.State["mismatch_class"]);
        Assert.Null(record.Exception);
        var rendered = string.Join("|", record.State.Values);
        Assert.DoesNotContain(org.ToString(), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(membership.ToString(), rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("private-other-subject", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-test-token", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceData.View", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_manual_authorization_routes_integrity_event_without_listener_before_database_continuation()
    {
        var records = new Records(); var org = Guid.NewGuid(); var calls = 0; var databaseResolutions = 0;
        var credentials = WorkerCredentialFixture.For([org], WorkerCredentialEncoding.AdminAreaProfile);
        var bearer = (await credentials.ReadAsync(default)).Token;
        var bearerHash = WorkerCredentialFixture.TokenSha256(bearer);
        const string returnedSubject = "different-synthetic-worker";
        TenantContextScope? observedTenant = null;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenantAuthorization:AuthManagementUrl"] = WorkerCredentialFixture.Issuer,
            ["TenantWorker:SubjectId"] = WorkerCredentialFixture.Subject.ToString("D"),
            ["TenantWorker:OrganisationIds:0"] = org.ToString()
        }).Build();
        using var host = new HostBuilder().ConfigureLogging(logs => logs.ClearProviders().AddProvider(records))
            .ConfigureServices((_, services) =>
            {
                services.AddApplication(); services.AddTenantEnforcement(configuration);
                services.AddScoped<TenantContextScope>(_ => observedTenant = new TenantContextScope());
                services.AddScoped<ApplicationDbContext>(_ =>
                {
                    databaseResolutions++;
                    throw new InvalidOperationException("Database continuation is forbidden in this authority-refusal fixture.");
                });
                services.Configure<HttpClientFactoryOptions>("TenantWorkerAccess", options =>
                    options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = new Handler(request =>
                    {
                        calls++;
                        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                        Assert.Equal(bearerHash, WorkerCredentialFixture.TokenSha256(request.Headers.Authorization?.Parameter));
                        return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                        {
                            ContractVersion = 1, SubjectId = returnedSubject, OrganisationId = org,
                            OrganisationMembershipId = Guid.NewGuid(), EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" },
                            DecisionVersion = "v1", ObservedAtUtc = DateTimeOffset.UtcNow
                        }) };
                    })));
            }).Build();
        var source = (ActivitySource)typeof(TenantAccessDiagnostics).GetField("Signals", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        Assert.False(source.HasListeners());
        var worker = new StaffProjectionRetryWorker(host.Services.GetRequiredService<IServiceScopeFactory>(), configuration,
            host.Services.GetRequiredService<ILogger<StaffProjectionRetryWorker>>(), credentials);
        var failed = await worker.DispatchCycleAsync(default);
        Assert.True(failed); Assert.Equal(1, calls); Assert.Equal(0, databaseResolutions);
        Assert.NotNull(observedTenant); Assert.Throws<TenantContextException>(() => observedTenant.Current);
        var record = Assert.Single(records.Items.Where(r => r.Event.Name == TenantAccessDiagnostics.IntegrityMismatch));
        Assert.Equal("subject_mismatch", record.State["mismatch_class"]);
        Assert.Equal("AdminAreaManagement", record.State["service"]);
        Assert.Null(record.Exception);
        var safe = string.Join("|", record.State.Values);
        Assert.DoesNotContain(org.ToString(), safe, StringComparison.Ordinal);
        Assert.DoesNotContain(WorkerCredentialFixture.Subject.ToString("D"), safe, StringComparison.Ordinal);
        Assert.DoesNotContain(returnedSubject, safe, StringComparison.Ordinal);
        Assert.False(safe.Contains(bearer, StringComparison.Ordinal), "Worker bearer appeared in diagnostic state.");
        Assert.DoesNotContain("TeamConfiguration.ManageStaffProfiles", safe, StringComparison.Ordinal);
        // The BackgroundService was never started: this is one controlled method invocation only.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Independent_operational_channel_failure_does_not_suppress_the_remaining_signal(bool throwLogger)
    {
        using var invocation = new Activity("admin-independent-operational-route").Start();
        var events = new List<ActivityEvent>(); var logger = new ChannelLogger(throwLogger);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TenantAccessDiagnostics.DiagnosticsName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.TraceId != invocation.TraceId || activity.OperationName != "tenant_access.diagnostic") return;
                if (!throwLogger) throw new InvalidOperationException("private-activity-exporter-error");
                events.AddRange(activity.Events);
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
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Calls++;
            if (shouldThrow) throw new InvalidOperationException("private-logger-error");
            Recorded.Add(eventId);
        }
    }

    private sealed record Credential(string? BearerToken = "synthetic-test-token") : ITenantAccessCredential;
    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
    private sealed class BrokenStream(Action read, IOException error) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { read(); throw error; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { read(); return ValueTask.FromException<int>(error); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class Records(bool throwIntegrity = false) : ILoggerProvider
    {
        public readonly List<LogRecord> Items = [];
        public int IntegrityCalls;
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this, throwIntegrity);
        public void Dispose() { }
        private sealed class RecordingLogger(Records records, bool throwIntegrity) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Name == TenantAccessDiagnostics.IntegrityMismatch) Interlocked.Increment(ref records.IntegrityCalls);
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    lock (records.Items) records.Items.Add(new(eventId, values.ToDictionary(v => v.Key, v => v.Value), exception));
                if (throwIntegrity && eventId.Name == TenantAccessDiagnostics.IntegrityMismatch)
                    throw new InvalidOperationException("private-logger-exporter-error");
            }
        }
    }
    private sealed record LogRecord(EventId Event, Dictionary<string, object?> State, Exception? Exception);
}
