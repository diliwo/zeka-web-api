using AdminAreaManagement.Application.Common.Services;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Application.Cities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.Infrastructure.Persistence.Configurations;
using AdminAreaManagement.Infrastructure.Persistence.Helpers;
using AdminAreaManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AdminAreaManagement.Application.Common.Authorization;
using Microsoft.Extensions.Hosting;
using Zeka.PersistenceSecurity;



namespace AdminAreaManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfigurationManager configuration)
    {
        services.AddSingleton(x => new FileRepositorySettings(configuration.GetValue<string>("FileServerPath")));

        services.AddTenantEnforcement(configuration);
        var runtimeConnection = configuration.GetConnectionString("ClientApiConnection");
        if (!string.IsNullOrWhiteSpace(runtimeConnection))
            services.AddSingleton<IHostedService>(_ => new RuntimeDatabaseIdentityValidator(
                runtimeConnection, "zeka_adminarea_runtime"));
        services.AddScoped<TenantTransactionAttemptState>();
        services.AddScoped<TenantPostCommitActions>();
        services.AddScoped<TenantCommandGuard>();
        services.AddScoped<ITenantTransactionExecutor, TenantTransactionExecutor>();
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
            options.UseNpgsql(
                configuration.GetConnectionString("ClientApiConnection"),
                builder => builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                    .EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(200), null))
                .AddInterceptors(provider.GetRequiredService<TenantCommandGuard>()));



        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();
        services.AddScoped<ICityQueries, CityQueries>();
        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        return services;
    }
}
