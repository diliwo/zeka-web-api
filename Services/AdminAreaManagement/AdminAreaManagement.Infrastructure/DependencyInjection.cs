using AdminAreaManagement.Application.Common.Services;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.Infrastructure.Persistence.Configurations;
using AdminAreaManagement.Infrastructure.Persistence.DbContextExtensions;
using AdminAreaManagement.Infrastructure.Persistence.Helpers;
using AdminAreaManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AdminAreaManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfigurationManager configuration)
    {
        services.AddSingleton(x => new FileRepositorySettings(configuration.GetValue<string>("FileServerPath")));

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(
                    configuration.GetConnectionString("DefaultApiConnection"),
                    b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

            // to revert to the pre-6.0 behavior to avoid the timeZone mapping
            AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();
        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        return services;
    }
    public static void UseMigrationsAndSeed(this IApplicationBuilder app)
    {
        using (var serviceScope = app.ApplicationServices.GetRequiredService<IServiceScopeFactory>().CreateScope())
        {
            if (!serviceScope.ServiceProvider.GetService<ApplicationDbContext>().Database.GetPendingMigrations().Any())
            {
                serviceScope.ServiceProvider.GetService<ApplicationDbContext>()?.Database.Migrate();
                serviceScope.ServiceProvider.GetService<ApplicationDbContext>()?.EnsureSeeded();
            }

        }
    }
}