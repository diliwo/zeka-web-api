using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Closures;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Messaging;
using AdminAreaManagement.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using Infrastructure.IntegrationTests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.RabbitMq;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Lifecycle.Contracts;
using Zeka.Messaging;

static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
static string Input(string name) => Environment.GetEnvironmentVariable(name) ?? throw new Exception("Missing fixture input: " + name);
static ServiceProvider Provider(string connection, Guid organisation, string? recovery = null)
{
    var configuration = new ConfigurationManager();
    configuration["ConnectionStrings:ClientApiConnection"] = connection;
    configuration["FileServerPath"] = Path.Combine(Path.GetTempPath(), "zeka-p1-unused-fixture-files");
    configuration["TenantWorker:OrganisationIds:0"] = organisation.ToString();
    var services = new ServiceCollection(); services.AddSingleton<IConfiguration>(configuration);
    services.AddInfrastructure(configuration);
    if (recovery is not null) services.AddAdminAreaFixtureClosureParticipants(organisation, recovery);
    return services.BuildServiceProvider();
}
static void Establish(AsyncServiceScope scope, Guid organisation) => scope.ServiceProvider.GetRequiredService<ITenantContextInitializer>()
    .Establish(new TenantContext(new TenantId(organisation), "p1-synthetic-fixture"));
static RabbitMqTopologyDefinition Definition(string suffix) => StaffProjectionTransport.CreateDefinition() with
{
    Exchange = "zeka-p1-v130-" + suffix, Queue = "zeka-p1-v130-" + suffix + ".q",
    DeadLetterExchange = "zeka-p1-v130-" + suffix + ".dlx", DeadLetterQueue = "zeka-p1-v130-" + suffix + ".dlq"
};
static RabbitMqOptions Broker(int port, int management) => new()
{
    HostName = "127.0.0.1", Port = port, UserName = "fixture", Password = Input("P1_BROKER_PASSWORD"),
    ManagementEndpoint = new Uri($"http://127.0.0.1:{management}/"), PublishTimeout = TimeSpan.FromSeconds(20)
};
static async Task<object?> Scalar(NpgsqlConnection connection, string sql, Guid org, NpgsqlTransaction? tx = null)
{
    await using var command = new NpgsqlCommand(sql, connection, tx); command.Parameters.AddWithValue("org", org);
    return await command.ExecuteScalarAsync();
}

if (args.Length > 0 && args[0] == "child")
{
    var mode = args[1]; var organisation = Guid.Parse(Input("P1_ORG"));
    await using var provider = Provider(Input("P1_DB"), organisation);
    await using var scope = provider.CreateAsyncScope(); Establish(scope, organisation);
    var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var executor = scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>();
    var options = Broker(int.Parse(Input("P1_AMQP")), int.Parse(Input("P1_HTTP")));
    using var consumerConnection = new RabbitMqConnection(options);
    using var topology = new RabbitMqTopology(consumerConnection, options, Definition(Input("P1_SUFFIX")));
    if (mode == "topology")
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        options.ManagementEndpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        _ = Task.Run(async () => { using var socket = await listener.AcceptTcpClientAsync(); Fault.Stall("topology"); });
        // Existing topology retains its original HTTP endpoint; reconstruct after controlled transport setup.
    }
    using var publishTopology = new RabbitMqTopology(consumerConnection, options, Definition(Input("P1_SUFFIX")));
    var lifecycle = new PublicationLifecycle(new PublisherFailStopOptions
    {
        EnableOwnerApprovedM1ProcessFailStop = true,
        AttemptDeadline = TimeSpan.FromSeconds(2), TerminationGrace = TimeSpan.FromSeconds(1)
    });
    var factory = new FaultFactory(options, mode);
    var bus = new RabbitMqEventBus(consumerConnection, new RabbitMqTelemetry(), publishTopology, options, lifecycle, factory);
    var outbox = new StaffProjectionOutbox(database, scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>(), bus,
        NullLogger<StaffProjectionOutbox>.Instance, scope.ServiceProvider.GetRequiredService<IConfiguration>(),
        scope.ServiceProvider.GetRequiredService<IAdminAreaClosureGate>());
    try
    {
        await executor.ExecuteAsync(async token =>
        {
            Console.WriteLine(JsonSerializer.Serialize(new { database_pid = ((NpgsqlConnection)database.Database.GetDbConnection()).ProcessID }));
            if (mode != "retry")
            {
                var row = await database.Set<StaffProjectionMessage>().SingleAsync(token);
                row.LastModified = new DateTime(2030, 1, 1);
                await database.SaveChangesAsync(token); // Must roll back on process death.
            }
            await outbox.DispatchAsync(token);
        }, default);
        Console.WriteLine("RETRY_COMMITTED"); return 0;
    }
    catch (InvalidOperationException) when (mode == "retry") { Console.WriteLine("CLOSURE_DENIED"); return 12; }
}

Environment.SetEnvironmentVariable("P1_BROKER_PASSWORD", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
var results = new List<object>();
var fixture = new PostgreSqlRlsRuntimeDatabase();
await using var broker = new ContainerBuilder("rabbitmq:3.13.7-management")
    .WithEnvironment("RABBITMQ_DEFAULT_USER", "fixture").WithEnvironment("RABBITMQ_DEFAULT_PASS", Input("P1_BROKER_PASSWORD"))
    .WithPortBinding(5672, true).WithPortBinding(15672, true)
    .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5672).UntilInternalTcpPortIsAvailable(15672)).Build();
try
{
    await fixture.InitializeAsync(); await fixture.ProvisionNonProductionRecoveryIdentityAsync(); await broker.StartAsync();
    var port = broker.GetMappedPublicPort(5672); var http = broker.GetMappedPublicPort(15672);
    var options = Broker(port, http);
    using var connection = new RabbitMqConnection(options);
    foreach (var mode in new[] { "before-channel", "topology", "after-channel", "pre-commit", "post-commit", "missing-confirm" })
    {
        var organisation = Guid.NewGuid(); var suffix = Guid.NewGuid().ToString("N");
        using var topology = new RabbitMqTopology(connection, options, Definition(suffix)); await topology.InitializeAsync();
        await using var provider = Provider(fixture.RuntimeConnectionString, organisation, fixture.RecoveryConnectionString);
        var message = new StaffProjectionChangedV1(organisation, Guid.NewGuid(), 1, true, "Synthetic", "Worker", "synthetic", "Team", "SYN");
        await using (var scope = provider.CreateAsyncScope())
        {
            Establish(scope, organisation);
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>().ExecuteAsync(async token =>
            {
                var row = new StaffProjectionMessage { EventId = message.Id, Payload = JsonSerializer.Serialize(message) };
                row.AssignToOrganisation(organisation); db.Add(row); await db.SaveChangesAsync(token);
            }, default);
        }
        Process Start(string childMode)
        {
            var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); info.ArgumentList.Add("child"); info.ArgumentList.Add(childMode);
            info.Environment["P1_ORG"] = organisation.ToString(); info.Environment["P1_DB"] = fixture.RuntimeConnectionString;
            info.Environment["P1_AMQP"] = port.ToString(); info.Environment["P1_HTTP"] = http.ToString(); info.Environment["P1_SUFFIX"] = suffix;
            info.Environment["COMPlus_DbgEnableMiniDump"] = "0";
            return Process.Start(info)!;
        }
        using var child = Start(mode);
        var errorTask = child.StandardError.ReadToEndAsync();
        var processId = 0;
        while (true)
        {
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(line is not null, "child exited before stall");
            if (line!.StartsWith('{')) processId = JsonDocument.Parse(line).RootElement.GetProperty("database_pid").GetInt32();
            if (line.StartsWith("STALL:")) break;
        }
        await using var observer = new NpgsqlConnection(fixture.AdministratorConnectionString); await observer.OpenAsync();
        await using var closure = await observer.BeginTransactionAsync();
        var closureLock = Scalar(observer, "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@org::text, 4602))", organisation, closure);
        await Task.Delay(120); Check(!closureLock.IsCompleted, "closure crossed a live publisher transaction");
        await using (var monitor = new NpgsqlConnection(fixture.AdministratorConnectionString))
        {
            await monitor.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_locks WHERE pid=@pid AND locktype='advisory' AND NOT granted)", monitor);
            command.Parameters.AddWithValue("pid", observer.ProcessID);
            Check((bool)(await command.ExecuteScalarAsync())!, "DB did not observe closure waiting for advisory lock");
        }
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Check(child.ExitCode != 0 && (await errorTask).Contains("ZEKA_M1_PUBLISHER_TERMINALITY_UNPROVEN"), "native failstop absent");
        await closureLock.WaitAsync(TimeSpan.FromSeconds(5));
        await closure.CommitAsync();
        await using (var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid=@pid", observer))
        { command.Parameters.AddWithValue("pid", processId); Check((long)(await command.ExecuteScalarAsync())! == 0, "publisher session survived"); }
        var pending = await Scalar(observer, "SELECT count(*) FROM public.\"StaffProjectionOutbox\" WHERE \"OrganisationId\"=@org AND \"PublishedAtUtc\" IS NULL AND (\"LastModified\" IS NULL OR \"LastModified\" < '2030-01-01')", organisation);
        Check((long)pending! == 1, "rollback/pending row missing");
        var eventId = await Scalar(observer, "SELECT \"EventId\" FROM public.\"StaffProjectionOutbox\" WHERE \"OrganisationId\"=@org", organisation);
        Check((Guid)eventId! == message.Id, "original EventId changed");
        using var channel = connection.Connection.CreateModel();
        if (mode == "topology")
        {
            await using var closureScope = provider.CreateAsyncScope(); Establish(closureScope, organisation);
            var operation = Guid.NewGuid();
            var header = new LifecycleMessageHeaderV1(operation, organisation, 2, AdminAreaClosureContractV1.ParticipantId,
                LifecycleContractV1.Version, LifecycleMessageIdentityV1.ForPhase(operation, AdminAreaClosureContractV1.ParticipantId, "enter-fence", 2), operation, operation);
            await closureScope.ServiceProvider.GetRequiredService<IAdminAreaClosureParticipant>().EnterFenceAsync(new(header, DateTimeOffset.UtcNow));
            using var denied = Start("retry"); var output = denied.StandardOutput.ReadToEndAsync(); var err = denied.StandardError.ReadToEndAsync();
            await denied.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await err;
            Check(denied.ExitCode == 12 && (await output).Contains("CLOSURE_DENIED"), "closed org replay was not denied");
            Check(channel.BasicGet(topology.Definition.Queue, true) is null, "pre-send topology stall emitted a message");
        }
        else
        {
            // Controlled supervisor backoff; no automatic unbounded crash loop.
            await Task.Delay(250);
            using var retry = Start("retry"); var output = retry.StandardOutput.ReadToEndAsync(); var err = retry.StandardError.ReadToEndAsync();
            await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); await err;
            Check(retry.ExitCode == 0 && (await output).Contains("RETRY_COMMITTED"), "same-ID retry did not commit");
            var ids = new List<Guid>(); BasicGetResult? delivery;
            while ((delivery = channel.BasicGet(topology.Definition.Queue, true)) is not null) ids.Add(Guid.Parse(delivery.BasicProperties.MessageId));
            Check(ids.Count >= 1 && ids.All(x => x == message.Id), "retry broker identity mismatch");
            if (mode == "missing-confirm") Check(ids.Count == 2, "uncertain first send plus retry must expose duplicate copies");
        }
        results.Add(new { mode, status = "PASS", event_id = message.Id, child_exit = child.ExitCode, rollback = true,
            pending_original_id = true, closure_waited_for_session_loss = true, retry = mode == "topology" ? "closed-org-denied" : "original-id" });
        Console.WriteLine(JsonSerializer.Serialize(results[^1]));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { status = "PASS", cases = results.Count, client_business_effect = "NOT_RUN", actual_socket_block = "NOT_RUN", runtime = Environment.Version.ToString() }));
    return 0;
}
catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { status = "FAIL", type = ex.GetType().Name, message = ex.Message })); return 1; }
finally { await fixture.DisposeAsync(); }

public static class Fault
{
    public static void Stall(string stage) { Console.WriteLine("STALL:" + stage); Thread.Sleep(Timeout.Infinite); }
}
public sealed class FaultFactory(RabbitMqOptions options, string mode) : IRabbitMqPublicationTransportFactory
{
    public IRabbitMqPublicationTransport Create() => new Transport(new RabbitMqPublicationTransportFactory(options).Create(), mode);
    private sealed class Transport(IRabbitMqPublicationTransport owner, string mode) : IRabbitMqPublicationTransport
    {
        private IConnection? connection;
        public void Open()
        {
            if (mode == "before-channel") Fault.Stall(mode);
            owner.Open();
            connection = Forward<IConnection>.Wrap(owner.Connection, (method, values, forward) =>
            {
                var result = forward();
                if (method.Name != "CreateModel") return result;
                if (mode == "after-channel") Fault.Stall(mode);
                return Forward<IModel>.Wrap((IModel)result!, (m, v, next) =>
                {
                    if (m.Name == "add_ModelShutdown" && mode == "pre-commit") { var registered = next(); Fault.Stall(mode); return registered; }
                    if (m.Name == "BasicPublish" && mode == "post-commit") Fault.Stall(mode);
                    if (m.Name == "add_BasicAcks" && mode == "missing-confirm") return null;
                    var output = next();
                    if (m.Name == "BasicPublish" && mode == "missing-confirm") Console.WriteLine("STALL:missing-confirm");
                    return output;
                });
            });
        }
        public IConnection Connection => connection!;
        public void Revoke() { if (mode == "missing-confirm") Fault.Stall("owned-revocation"); owner.Revoke(); }
        public bool IsTerminal => owner.IsTerminal;
        public void Dispose() => owner.Dispose();
    }
}
public class Forward<T> : DispatchProxy where T : class
{
    private T target = null!;
    private Func<MethodInfo, object?[]?, Func<object?>, object?> intercept = null!;
    public static T Wrap(T target, Func<MethodInfo, object?[]?, Func<object?>, object?> intercept)
    { var proxy = Create<T, Forward<T>>(); var state = (Forward<T>)(object)proxy; state.target = target; state.intercept = intercept; return proxy; }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => intercept(method!, args, () =>
    {
        try { return method!.Invoke(target, args); }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    });
}
