using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.EventBus.Abstractions;
using Zeka.Extensions.EventBus.RabbitMq;

namespace Zeka.Messaging;

/// <summary>One nonsecret M1 compatibility contract, linked into both service composition roots.</summary>
public static class StaffProjectionTransport
{
    public const string Exchange = "zeka-m1-v130";
    public const string Queue = "zeka-m1-client-v130";
    public const string DeadLetterExchange = "zeka-m1-v130.dlx";
    public const string DeadLetterQueue = "zeka-m1-client-v130.dlq";

    public static RabbitMqTopologyDefinition CreateDefinition() => new()
    {
        Exchange = Exchange, Queue = Queue,
        DeadLetterExchange = DeadLetterExchange, DeadLetterQueue = DeadLetterQueue,
        // Preserve Client's existing second handler without coupling AdminArea to its application assembly.
        RoutingKeys = [nameof(StaffProjectionChangedV1), "SocialWorkerCreatedEvent"],
        MaxLength = 1000, DeadLetterMaxLength = 1000
    };

    public static IServiceCollection AddStaffProjectionTransport(this IServiceCollection services,
        IConfigurationManager configuration)
    {
        var definition = CreateDefinition();
        var section = configuration.GetSection(RabbitMqTopologyDefinition.SectionName);
        foreach (var (key, expected) in new Dictionary<string, string>
        {
            [nameof(definition.Exchange)] = definition.Exchange,
            [nameof(definition.Queue)] = definition.Queue,
            [nameof(definition.DeadLetterExchange)] = definition.DeadLetterExchange,
            [nameof(definition.DeadLetterQueue)] = definition.DeadLetterQueue,
            [nameof(definition.MaxLength)] = definition.MaxLength.ToString(CultureInfo.InvariantCulture),
            [nameof(definition.DeadLetterMaxLength)] = definition.DeadLetterMaxLength.ToString(CultureInfo.InvariantCulture)
        })
            if (section[key] is { } supplied && !string.Equals(supplied, expected, StringComparison.Ordinal))
                throw new InvalidOperationException("RabbitMqTopology must match the shared staff projection contract.");
        var routes = section.GetSection(nameof(definition.RoutingKeys)).Get<string[]>();
        if (routes is not null && !routes.Order(StringComparer.Ordinal)
                .SequenceEqual(definition.RoutingKeys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("RabbitMqTopology routing keys must match the shared staff projection contract.");

        services.AddRabbitMqEventBus(configuration);
        services.Replace(ServiceDescriptor.Singleton(definition));
        // .NET8 invokes every StartingAsync before any hosted StartAsync, including the outbox worker.
        // Resolve the broker lazily at that boundary, not while constructing hosted services.
        services.AddSingleton<IHostedService>(provider => new StaffProjectionTopologyInitializer(
            ct => provider.GetRequiredService<RabbitMqTopology>().InitializeAsync(ct)));
        return services;
    }

    public static IServiceCollection AddStaffProjectionConsumer(this IServiceCollection services,
        IConfigurationManager configuration)
    {
        services.AddRabbitMqSubscriberService(configuration);
        // Select the NEW shared versioned queue; never redeclare or delete the historical queue.
        services.PostConfigure<EventBusOptions>(options => options.QueueName = Queue);
        return services;
    }
}

public sealed class StaffProjectionTopologyInitializer(Func<CancellationToken, Task> initialize)
    : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => initialize(cancellationToken);
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
