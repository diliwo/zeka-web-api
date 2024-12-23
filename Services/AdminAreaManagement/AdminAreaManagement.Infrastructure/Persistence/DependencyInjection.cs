using AdminAreaManagement.Application.Common.Helpers;
using AdminAreaManagement.Application.Common.Services;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence.Configurations;
using AdminAreaManagement.Infrastructure.Persistence.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using DateTimeService = AdminAreaManagement.Infrastructure.Services.DateTimeService;

namespace AdminAreaManagement.Infrastructure.Persistence;

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

        //services.AddTransient<IFileService, FileService>();
        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<ISortHelper<TrainingType>, SortHelper<TrainingType>>();
        services.AddScoped<ISortHelper<TrainingField>, SortHelper<TrainingField>>();
        services.AddScoped<ISortHelper<StaffMember>, SortHelper<StaffMember>>();
        services.AddScoped<ISortHelper<Team>, SortHelper<Team>>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();

        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        return services;
    }
}