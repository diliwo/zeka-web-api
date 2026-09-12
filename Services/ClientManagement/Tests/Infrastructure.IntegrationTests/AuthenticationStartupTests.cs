using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class AuthenticationStartupTests : Zeka.Authentication.Tests.RealHostStartupTests, IAsyncLifetime
{
    private readonly INetwork network = new NetworkBuilder().Build();
    private IContainer? broker;
    private IContainer? host;
    private string queueName = "";

    protected override string ApiDirectory => "Services/ClientManagement/Client.API";
    protected override string AssemblyName => "ClientManagement.API";

    public async Task InitializeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await network.CreateAsync(timeout.Token);
        broker = new ContainerBuilder("rabbitmq:3.13.7-alpine")
            .WithNetwork(network)
            .WithNetworkAliases("startup-rabbitmq")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Server startup complete")
                .UntilCommandIsCompleted("rabbitmq-diagnostics", "-q", "check_port_connectivity"))
            .Build();
        await broker.StartAsync(timeout.Token);
    }

    protected override async Task PrepareHostAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        // The production adapter accepts a hostname only (AMQP port 5672).
        // A private network avoids fixed host ports and any developer's broker.
        // Run the same real API entry point; do not replace its registrations.
        var assembly = start.ArgumentList[0];
        queueName = start.Environment["EventBus__QueueName"]!;
        var environment = start.Environment
            .Where(pair => pair.Key.Contains("__", StringComparison.Ordinal)
                || pair.Key is "ASPNETCORE_ENVIRONMENT" or "DOTNET_ENVIRONMENT")
            .ToDictionary(pair => pair.Key, pair => pair.Value!);
        environment["RabbitMq__HostName"] = "startup-rabbitmq";
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
            if (result.Stdout.Split('\n').Any(line => line.Trim() == queueName + "\t1")) return;
            await Task.Delay(100, cancellationToken);
        }
    }

    public async Task DisposeAsync()
    {
        try { if (host is not null) await host.DisposeAsync(); }
        finally
        {
            try { if (broker is not null) await broker.DisposeAsync(); }
            finally { await network.DisposeAsync(); }
        }
    }
}
