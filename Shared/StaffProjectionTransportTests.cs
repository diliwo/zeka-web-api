using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.EventBus.RabbitMq;
using Zeka.Messaging;

namespace Infrastructure.IntegrationTests;

// Composition/lifecycle tests only. No broker, Auth or persisted projection conformance is claimed.
public sealed class StaffProjectionTransportTests
{
    [Fact]
    public void Bootstrap_publisher_and_consumer_select_one_versioned_contract_without_opening_a_connection()
    {
        var configuration = new ConfigurationManager();
        configuration["EventBus:QueueName"] = "historical-client-queue";
        var services = new ServiceCollection();
        services.AddStaffProjectionTransport(configuration).AddStaffProjectionConsumer(configuration);
        using var provider = services.BuildServiceProvider();
        var definition = provider.GetRequiredService<RabbitMqTopologyDefinition>();
        Assert.Single(services.Where(d => d.ServiceType == typeof(RabbitMqTopologyDefinition)));
        Assert.Equal(StaffProjectionTransport.Queue, definition.Queue);
        Assert.Equal(StaffProjectionTransport.Exchange, definition.Exchange);
        Assert.Equal(StaffProjectionTransport.DeadLetterQueue, definition.DeadLetterQueue);
        Assert.Equal(StaffProjectionTransport.DeadLetterExchange, definition.DeadLetterExchange);
        Assert.Equal(new[] { "StaffProjectionChangedV1", "SocialWorkerCreatedEvent" }, definition.RoutingKeys);
        Assert.Equal(1000, definition.MaxLength);
        Assert.Equal(1000, definition.DeadLetterMaxLength);
        Assert.Equal(5, RabbitMqTopologyDefinition.DeliveryLimit);
        Assert.Equal(10, RabbitMqTopologyDefinition.Prefetch);
        Assert.Equal(definition.Queue, provider.GetRequiredService<IOptions<EventBusOptions>>().Value.QueueName);
        Assert.Single(provider.GetServices<IHostedService>().OfType<StaffProjectionTopologyInitializer>());
    }

    [Theory]
    [InlineData("Queue", "different-v130-queue")]
    [InlineData("Exchange", "different-v130-exchange")]
    [InlineData("DeadLetterExchange", "different-dlx")]
    [InlineData("DeadLetterQueue", "different-dlq")]
    [InlineData("MaxLength", "0")]
    [InlineData("DeadLetterMaxLength", "2000")]
    [InlineData("RoutingKeys:0", "unrelated-event")]
    public void Conflicting_service_local_topology_fails_closed(string key, string value)
    {
        var configuration = new ConfigurationManager();
        configuration["RabbitMqTopology:" + key] = value;
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddStaffProjectionTransport(configuration));
    }

    [Fact]
    public async Task Topology_starting_phase_completes_before_even_an_earlier_registered_publisher_starts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerStarted = false;
        using var host = new HostBuilder().ConfigureServices((_, services) =>
        {
            services.AddSingleton<IHostedService>(new Producer(() => producerStarted = true));
            services.AddSingleton<IHostedService>(new StaffProjectionTopologyInitializer(async ct =>
            { entered.TrySetResult(); await release.Task.WaitAsync(ct); }));
        }).Build();
        var starting = host.StartAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(producerStarted);
            Assert.False(starting.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await starting;
        Assert.True(producerStarted);
        await host.StopAsync();
    }

    [Fact]
    public async Task Failed_topology_initialization_prevents_any_hosted_publisher_start()
    {
        var producerStarted = false;
        using var host = new HostBuilder().ConfigureServices((_, services) =>
        {
            services.AddSingleton<IHostedService>(new Producer(() => producerStarted = true));
            services.AddSingleton<IHostedService>(new StaffProjectionTopologyInitializer(
                _ => Task.FromException(new InvalidOperationException("Synthetic topology refusal"))));
        }).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
        Assert.False(producerStarted);
    }

    private sealed class Producer(Action start) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) { start(); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
