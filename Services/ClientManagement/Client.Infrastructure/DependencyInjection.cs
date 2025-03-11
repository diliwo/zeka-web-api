using ClientManagement.Application.Common.Helpers;
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
        services.Configure<TenantSettings>(configuration.GetSection(nameof(TenantSettings)));

        var options = services.GetOptions<TenantSettings>(nameof(TenantSettings));
        var defaultConnectionString = options.DefaultConnectionString;
        services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));



        // to revert to the pre-6.0 behavior to avoid the timeZone mapping
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        services.AddTransient<IMonitoringActionRepository, MonitoringActionRepository>(); ;
        services.AddTransient<ILanguageRepository, LanguageRepository>(); 
        services.AddHttpContextAccessor();
        //services.AddTransient<IFileService, FileService>();
        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<ISortHelper<SchoolRegistration>, SortHelper<SchoolRegistration>>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();
        services.AddTransient<ITenantService, TenantService>();
        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        services.Configure<GenericReadRepository<ReasonOfClosure>>(configuration.GetSection(ConfigurationKeys.ReasonOfClosure));
        services.AddSingleton<IGenericReadRepository<ReasonOfClosure>, GenericReadRepository<ReasonOfClosure>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<ReasonOfClosure>>>().Value); // TODO : move into RepositoryManager

    
        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        var tenants = options.Tenants;
        foreach (var tenant in tenants)
        {
            string connectionString;
            if (string.IsNullOrEmpty(tenant.ConnectionString))
            {
                connectionString = defaultConnectionString;
            }
            else
            {
                connectionString = tenant.ConnectionString;
            }
            using var scope = services.BuildServiceProvider().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            dbContext.Database.SetConnectionString(connectionString);
            if (dbContext.Database.GetMigrations().Count() > 0)
            {
                dbContext.Database.Migrate();
            }
        }

        return services;
    }

    public static T GetOptions<T>(this IServiceCollection services, string sectionName) where T : new()
    {
        using var serviceProvider = services.BuildServiceProvider();
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var section = configuration.GetSection(sectionName);
        var options = new T();
        section.Bind(options);
        return options;
    }
}