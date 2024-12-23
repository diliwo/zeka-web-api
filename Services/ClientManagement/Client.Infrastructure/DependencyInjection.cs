using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using ClientManagement.Core.ValueObjects;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Infrastructure.Persistence.Configurations;
using ClientManagement.Infrastructure.Persistence.Helpers;
using ClientManagement.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClientManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(x => new FileRepositorySettings(configuration.GetValue<string>("FileServerPath")));

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(
                    configuration.GetConnectionString("ClientApiConnection"),
                    b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

            // to revert to the pre-6.0 behavior to avoid the timeZone mapping
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        services.AddTransient<IMonitoringActionRepository, MonitoringActionRepository>(); ;
        services.AddTransient<ILanguageRepository, LanguageRepository>();
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

    
        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        return services;
    }
}