using AuthManager.API.Endpoints;
using AuthManager.Application;
using AuthManager.Application.Lifecycle;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Persistence.Lifecycle;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.IntegrationTests;

public sealed class Life05aProductionIsolationTests
{
    [Fact]
    public void Life05aFailClosed_production_composition_has_no_verifier_finalizer_route_or_worker()
    {
        var config = new ConfigurationManager();
        config["ConnectionStrings:Default"] =
            "Host=localhost;Database=synthetic_no_connection;Username=zeka_auth_runtime;Password=unused";
        var services = new ServiceCollection();
        services.Infrastructure(config);
        services.Application();
        using var provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<LifecycleVerificationCoordinator>());
        Assert.Null(provider.GetService<ILifecycleVerificationStore>());
        Assert.Null(provider.GetService<INonProductionPurgeVerifier>());
        Assert.Null(provider.GetService<INonProductionFixtureWriteFence>());
        Assert.Null(provider.GetService<INonProductionPurgeCapability>());
        Assert.Null(provider.GetService<PurgeFixtureScope>());
        Assert.Null(provider.GetService<LifecycleVerificationStore>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IHostedService)
            && (descriptor.ImplementationType?.Name.Contains("Verification", StringComparison.Ordinal)
                ?? false));
        var endpointMethods = typeof(AuthApiEndpoints).Assembly.GetTypes()
            .Where(type => type.Namespace?.Contains("Endpoints", StringComparison.Ordinal) == true)
            .SelectMany(type => type.GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
            .ToArray();
        Assert.DoesNotContain(endpointMethods, method => method.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(LifecycleVerificationCoordinator)
            || parameter.ParameterType == typeof(ILifecycleVerificationStore)
            || parameter.ParameterType == typeof(INonProductionPurgeVerifier)));
    }
}
