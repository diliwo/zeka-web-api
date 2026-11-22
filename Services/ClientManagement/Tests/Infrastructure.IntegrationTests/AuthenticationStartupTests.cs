using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using Zeka.Messaging;

namespace Infrastructure.IntegrationTests;

public sealed class AuthenticationStartupTests : Zeka.Authentication.Tests.RealHostStartupTests, IAsyncLifetime
{
    private const string RuntimePassword = "startup-runtime-password";
    private const string BrokerAlias = "startup-rabbitmq";
    private const string BrokerManagementPort = "15672";
    private const string BrokerUser = "startup-test";
    private readonly string brokerPassword = Guid.NewGuid().ToString("N");
    private readonly INetwork network = new NetworkBuilder().Build();
    private PostgreSqlContainer? postgres;
    private IContainer? broker;
    private IContainer? host;

    protected override string ApiDirectory => "Services/ClientManagement/Client.API";
    protected override string AssemblyName => "ClientManagement.API";

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await network.CreateAsync(timeout.Token);
        postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("startup-postgres")
            .Build();
        broker = new ContainerBuilder("rabbitmq:3.13.7-management")
            .WithNetwork(network)
            .WithNetworkAliases(BrokerAlias)
            .WithEnvironment("RABBITMQ_DEFAULT_USER", BrokerUser)
            .WithEnvironment("RABBITMQ_DEFAULT_PASS", brokerPassword)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Server startup complete")
                .UntilCommandIsCompleted("rabbitmq-diagnostics", "-q", "check_port_listener", BrokerManagementPort)
                .UntilCommandIsCompleted("rabbitmq-diagnostics", "-q", "check_port_connectivity"))
            .Build();
        await postgres.StartAsync(timeout.Token);
        await broker.StartAsync(timeout.Token);

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync(timeout.Token);
        await using var command = new NpgsqlCommand(ReadBootstrapScript(), connection);
        await command.ExecuteNonQueryAsync(timeout.Token);
        command.CommandText = $"ALTER ROLE zeka_client_runtime PASSWORD '{RuntimePassword}'";
        await command.ExecuteNonQueryAsync(timeout.Token);
    }

    protected override async Task PrepareHostAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        // Both AMQP and management HTTP stay on the private fixture network.
        // The real API performs the broker topology verification during startup.
        var assembly = start.ArgumentList[0];
        var environment = start.Environment
            .Where(pair => pair.Key.Contains("__", StringComparison.Ordinal)
                || pair.Key is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT")
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
        environment["RabbitMq__HostName"] = BrokerAlias;
        environment["RabbitMq__ManagementEndpoint"] = $"http://{BrokerAlias}:{BrokerManagementPort}/";
        environment["RabbitMq__UserName"] = BrokerUser;
        environment["RabbitMq__Password"] = brokerPassword;
        environment["ConnectionStrings__ClientApiConnection"] = new NpgsqlConnectionStringBuilder(
            postgres!.GetConnectionString())
        {
            Host = "startup-postgres",
            Port = 5432,
            Username = "zeka_client_runtime",
            Password = RuntimePassword
        }.ConnectionString;
        environment["ASPNETCORE_URLS"] = "http://0.0.0.0:8080";
        host = new ContainerBuilder("mcr.microsoft.com/dotnet/aspnet:8.0.30")
            .WithNetwork(network)
            .WithPortBinding(8080, true)
            .WithResourceMapping(new DirectoryInfo(Path.GetDirectoryName(assembly)!), "/app")
            .WithResourceMapping(new FileInfo(Path.Combine(start.WorkingDirectory, "appsettings.json")), "/app/appsettings.json")
            .WithEnvironment(environment)
            .WithWorkingDirectory("/app")
            .WithEntrypoint("sleep", "infinity")
            .Build();
        await host.StartAsync(cancellationToken);

        // Keep the shared process-exit, startup-log, real HTTP and fail-closed assertions.
        start.FileName = "docker";
        start.ArgumentList.Clear();
        // Sanitization deliberately removes Docker context selection and changes HOME.
        // Use the endpoint already resolved for our containers, including rootless
        // Unix sockets and Windows named pipes, rather than the CLI's default socket.
        // Preserve transport configuration references for TLS-enabled Docker daemons.
        foreach (var name in new[] { "DOCKER_CONFIG", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        // Docker CLI spells the same Windows pipe as a UNC URI, unlike Docker.DotNet.
        var dockerHost = endpoint.Scheme == "npipe"
            ? $"npipe:////{endpoint.Host}{endpoint.AbsolutePath}"
            : endpoint.ToString();
        foreach (var argument in new[] { "--host", dockerHost,
            "exec", host.Id, "dotnet", "/app/" + Path.GetFileName(assembly) })
            start.ArgumentList.Add(argument);
    }

    protected override string ResolveAddress(string address)
        => new UriBuilder("http", host!.Hostname, host.GetMappedPublicPort(8080)).Uri.ToString().TrimEnd('/');

    protected override async Task VerifyDependenciesAsync(CancellationToken cancellationToken)
    {
        // Prove the actual subscriber connected to this test's broker, not merely
        // that Kestrel listened while the background subscriber failed.
        while (true)
        {
            var result = await broker!.ExecAsync(["rabbitmqctl", "list_queues", "--quiet", "name", "consumers"], cancellationToken);
            Assert.Equal(0, result.ExitCode);
            if (result.Stdout.Split('\n').Any(line => line.Trim() == StaffProjectionTransport.Queue + "\t1")) return;
            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task DisposeAsync()
    {
        try { if (host is not null) await host.DisposeAsync(); }
        finally
        {
            try { if (broker is not null) await broker.DisposeAsync(); }
            finally
            {
                try { if (postgres is not null) await postgres.DisposeAsync(); }
                finally { await network.DisposeAsync(); }
            }
        }
    }

    private static string ReadBootstrapScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-client-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed Client role bootstrap script was not found.");
    }
}
