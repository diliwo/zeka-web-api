using System.Configuration;
using System.Reflection;
using Client.Core.Entities;
using Client.Core.Interfaces;
using Client.Core.ValueObjects;
using Client.Infrastructure.Persistence;
using Client.Infrastructure.Persistence.Configurations;
using Client.Infrastructure.Persistence.Helpers;
using Client.Infrastructure.Services;
using FluentAssertions.Common;
using Humanizer.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;


namespace Client.Infrastructure;

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
        services.AddScoped<ISortHelper<TrainingType>, SortHelper<TrainingType>>();
        services.AddScoped<ISortHelper<TrainingField>, SortHelper<TrainingField>>();
        services.AddScoped<ISortHelper<SchoolRegistration>, SortHelper<SchoolRegistration>>();
        services.AddScoped<ISortHelper<Staff>, SortHelper<Staff>>();
        services.AddScoped<ISortHelper<Team>, SortHelper<Team>>();
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