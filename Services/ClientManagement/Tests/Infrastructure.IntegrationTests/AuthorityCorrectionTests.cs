using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClientManagement.API;
using ClientManagement.API.Services;
using ClientManagement.Application;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.Authentication;
using Zeka.Authentication.Tests;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Named_dependency_failure_uses_server_correlation_and_preserves_empty_no_store_503(bool httpIo)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenBody(httpIo)) });
        using var client = new HttpClient(handler) { BaseAddress = new("https://auth.invalid/") };
        var scope = new TenantContextScope();
        var operation = new TenantOperation(new CurrentTenantAccessClient(client, new Credential()), new Identity(Guid.NewGuid()), scope, scope);
        using var records = new RecordingProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(records));
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        var serverCorrelation = context.TraceIdentifier;
        var callerValue = Guid.NewGuid().ToString("N");
        context.Request.Headers["X-Correlation-Id"] = callerValue;
        context.Request.Headers["X-Request-Id"] = callerValue;
        context.Response.Body = body;
        if (httpIo) context.Response.Headers.CacheControl = "no-store,no-cache";

        await new TenantAccessMiddleware(async _ =>
        {
            await operation.AuthorizeAsync(new("ReferenceData.View"), default);
            throw new InvalidOperationException("Authorization unexpectedly continued");
        }, factory.CreateLogger<TenantAccessMiddleware>()).InvokeAsync(context);

        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(httpIo ? "no-store,no-cache" : "no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, body.Length);
        Assert.ThrowsAny<InvalidOperationException>(() => scope.Current);
        Assert.InRange(handler.Calls, 1, 2);
        var entry = Assert.Single(records.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, entry.Event.Name);
        Assert.Equal(typeof(TenantAccessMiddleware).FullName, entry.Category);
        Assert.Null(entry.Exception);
        Assert.Equal(new[] { "event_code", "service", "request_correlation_id", "{OriginalFormat}" }, entry.Fields.Keys.ToArray());
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, entry.Fields["event_code"]);
        Assert.Equal("ClientManagement", entry.Fields["service"]);
        Assert.False(string.IsNullOrEmpty(serverCorrelation));
        Assert.Equal(serverCorrelation, entry.Fields["request_correlation_id"]);
        Assert.DoesNotContain(callerValue, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Named_dependency_logger_failure_cannot_change_the_response()
    {
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;
        var logger = new FailingMiddlewareLogger();

        await new TenantAccessMiddleware(_ => throw new TenantAccessException(AccessFailure.Unavailable,
            TenantAccessDiagnostics.DependencyUnavailable), logger).InvokeAsync(context);

        Assert.Equal(1, logger.Calls);
        Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, body.Length);
    }

    [Theory]
    [InlineData(AccessFailure.Unavailable, TenantAccessDiagnostics.IntegrityMismatch, 503)]
    [InlineData(AccessFailure.Unavailable, TenantAccessDiagnostics.ResponseMalformed, 503)]
    [InlineData(AccessFailure.Denied, TenantAccessDiagnostics.DependencyUnavailable, 403)]
    public async Task Other_tenant_failures_do_not_emit_auth_dependency_warning(AccessFailure failure, string reason, int status)
    {
        using var records = new RecordingProvider();
        using var factory = LoggerFactory.Create(logging => logging.AddProvider(records));
        using var body = new MemoryStream();
        var context = new DefaultHttpContext();
        context.Response.Body = body;

        await new TenantAccessMiddleware(_ => throw new TenantAccessException(failure, reason),
            factory.CreateLogger<TenantAccessMiddleware>()).InvokeAsync(context);

        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(0, body.Length);
        Assert.Empty(records.Entries);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, true)]
    [InlineData(HttpStatusCode.Unauthorized, false, false)]
    [InlineData(HttpStatusCode.Forbidden, false, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, false)]
    public async Task Client_challenge_logs_only_jwks_unavailability_once_with_server_correlation(
        HttpStatusCode expectedStatus, bool jwksUnavailable, bool throwDependency)
    {
        using var fixture = new AuthenticationFixture();
        using var records = new RecordingProvider(throwDependency: throwDependency);
        var source = new DocumentSource(fixture.Jwks()) { Unavailable = jwksUnavailable };
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Warning).AddProvider(records);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddTenantAuthentication(fixture.Configuration());
        builder.Services.AddSingleton<IJwksDocumentSource>(source);
        builder.Services.AddSingleton<TimeProvider>(fixture.Clock);

        await using var app = builder.Build();
        var correlations = new ConcurrentQueue<string>();
        var endpointCalls = 0;
        var repeatedChallenges = 0;
        app.Use(async (http, next) =>
        {
            correlations.Enqueue(http.TraceIdentifier);
            await next(http);
            if (jwksUnavailable && http.Response.StatusCode == StatusCodes.Status503ServiceUnavailable)
            {
                Interlocked.Increment(ref repeatedChallenges);
                await http.ChallengeAsync();
            }
        });
        app.UseAuthentication();
        app.UseMiddleware<TenantAccessMiddleware>();
        app.UseAuthorization();
        app.MapGet("/protected", () =>
        {
            Interlocked.Increment(ref endpointCalls);
            return Results.StatusCode((int)expectedStatus);
        }).RequireAuthorization();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/protected");
        var callerCorrelation = Guid.NewGuid().ToString("N");
        var subject = Guid.NewGuid().ToString();
        var token = fixture.Token(expectedStatus == HttpStatusCode.Unauthorized ? "issuer" : "", subject: subject);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Correlation-Id", callerCorrelation);
        request.Headers.Add("X-Request-Id", callerCorrelation);

        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        var challenged = jwksUnavailable || expectedStatus == HttpStatusCode.Unauthorized;
        Assert.Equal(challenged, response.Headers.CacheControl?.NoStore == true);
        if (expectedStatus == HttpStatusCode.Unauthorized)
            Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).ToString());
        else
            Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Equal(challenged ? 0 : 1, Volatile.Read(ref endpointCalls));
        Assert.Equal(jwksUnavailable ? 1 : 0, Volatile.Read(ref repeatedChallenges));
        Assert.Equal(expectedStatus == HttpStatusCode.Unauthorized ? 0 : 1, source.Calls);

        var named = records.Entries.Where(entry => entry.Event.Id == 1702 ||
            entry.Fields.TryGetValue("event_code", out var code) && Equals(code, TenantAccessDiagnostics.DependencyUnavailable));
        if (!jwksUnavailable)
        {
            Assert.Empty(named);
            return;
        }

        var entry = Assert.Single(named);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1702, entry.Event.Id);
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, entry.Event.Name);
        Assert.Equal(typeof(TenantAuthentication).FullName, entry.Category);
        Assert.Null(entry.Exception);
        Assert.Equal(new[] { "event_code", "service", "request_correlation_id", "{OriginalFormat}" }, entry.Fields.Keys.ToArray());
        Assert.Equal(TenantAccessDiagnostics.DependencyUnavailable, entry.Fields["event_code"]);
        Assert.Equal("ClientManagement", entry.Fields["service"]);
        var serverCorrelation = Assert.Single(correlations);
        Assert.False(string.IsNullOrEmpty(serverCorrelation));
        Assert.NotEqual(callerCorrelation, serverCorrelation);
        Assert.Equal(serverCorrelation, entry.Fields["request_correlation_id"]);
        Assert.DoesNotContain(callerCorrelation, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(subject, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(token, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-discovery-detail", entry.Message, StringComparison.Ordinal);
    }

    private sealed class FailingMiddlewareLogger : ILogger<TenantAccessMiddleware>
    {
        public int Calls { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Calls++;
            throw new InvalidOperationException("Synthetic logger failure");
        }
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
    private sealed class RecordingProvider(bool throwIntegrity = false, bool throwDependency = false) : ILoggerProvider
    {
        public ConcurrentBag<LogEntry> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries, throwIntegrity, throwDependency);
        public void Dispose() { }
    }
    private sealed class RecordingLogger(string category, ConcurrentBag<LogEntry> entries, bool throwIntegrity, bool throwDependency) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (throwIntegrity && eventId.Name == TenantAccessDiagnostics.IntegrityMismatch) throw new InvalidOperationException("private-logger-error");
            var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? values.ToDictionary(pair => pair.Key, pair => pair.Value)
                : new Dictionary<string, object?>();
            entries.Add(new(category, logLevel, eventId, fields, formatter(state, exception), exception));
            if (throwDependency && eventId.Name == TenantAccessDiagnostics.DependencyUnavailable)
                throw new InvalidOperationException("Synthetic named logger failure");
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