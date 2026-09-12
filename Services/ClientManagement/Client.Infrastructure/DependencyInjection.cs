using ClientManagement.Application.Common.Helpers;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Infrastructure.Persistence.Configurations;
using ClientManagement.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using ClientManagement.Application.Common.Authorization;
using Microsoft.Extensions.Hosting;
using Zeka.PersistenceSecurity;

namespace ClientManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(x => new FileRepositorySettings(configuration.GetValue<string>("FileServerPath")));

        services.AddTenantEnforcement(configuration);
        var runtimeConnection = configuration.GetConnectionString("ClientApiConnection");
        if (!string.IsNullOrWhiteSpace(runtimeConnection))
            services.AddSingleton<IHostedService>(_ => new RuntimeDatabaseIdentityValidator(
                runtimeConnection, "zeka_client_runtime"));
        services.AddScoped<TenantTransactionAttemptState>();
        services.AddScoped<TenantCommandGuard>();
        services.AddScoped<ITenantTransactionExecutor, TenantTransactionExecutor>();
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
            options.UseNpgsql(
                configuration.GetConnectionString("ClientApiConnection"),
                builder => builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                    .EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(200), null))
                .AddInterceptors(provider.GetRequiredService<TenantCommandGuard>()));

        services.AddTransient<IMonitoringActionRepository, MonitoringActionRepository>(); ;
        services.AddTransient<ILanguageRepository, LanguageRepository>();
        services.AddHttpContextAccessor();
        //services.AddTransient<IFileService, FileService>();
        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<ISortHelper<SchoolRegistration>, SortHelper<SchoolRegistration>>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();
        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        services.Configure<GenericReadRepository<ReasonOfClosure>>(configuration.GetSection(ConfigurationKeys.ReasonOfClosure));
        services.AddSingleton<IGenericReadRepository<ReasonOfClosure>, GenericReadRepository<ReasonOfClosure>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<ReasonOfClosure>>>().Value); // TODO : move into RepositoryManager

        return services;
    }
}
