using AuthManager.Application.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AuthManager.Infrastructure.Workers;

public static class WorkerRotationRegistration
{
    public static IServiceCollection AddM1WorkerRotation(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("M1WorkerRotation");
        services.AddSingleton(new WorkerRotationOptions
        {
            Enabled = section.GetValue<bool>("Enabled"), LocalStack = environment.IsEnvironment("LocalStack"),
            OwnerSubjectId = section.GetValue<Guid>("OwnerSubjectId"), OrganisationId = section.GetValue<Guid>("OrganisationId"),
            ProcessingTimeout = TimeSpan.FromSeconds(section.GetValue<int>("ProcessingTimeoutSeconds")),
            Retention = TimeSpan.FromSeconds(section.GetValue<int>("RetentionSeconds"))
        });
        services.AddScoped<WorkerIdentitySource>();
        services.AddScoped<IWorkerRotationStore, WorkerRotationStore>();
        services.AddScoped<WorkerRotationService>();
        return services;
    }
}
