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
using Tenant;
using Tenant.Models;

namespace AdminAreaManagement.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfigurationManager configuration)
    {
        services.AddSingleton(x => new FileRepositorySettings(configuration.GetValue<string>("FileServerPath")));

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("ClientApiConnection"),
                builder => builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)));

        // Compatibility for the legacy integration-event payload only. This does not
        // participate in DbContext registration or connection selection.
        services.Configure<TenantSettings>(configuration.GetSection("TenantSettings"));
        services.AddHttpContextAccessor();
        services.AddScoped<ITenantService, TenantService>();

        services.AddTransient<IDateTime, DateTimeService>();
        services.AddScoped<IRepositoryManager, RepositoryManager>();
        services.AddScoped<ICityQueries, CityQueries>();
        services.Configure<GenericReadRepository<Reward>>(configuration.GetSection(ConfigurationKeys.Rewards));
        services.AddSingleton<IGenericReadRepository<Reward>, GenericReadRepository<Reward>>(sp =>
            sp.GetRequiredService<IOptions<GenericReadRepository<Reward>>>().Value); // TODO : move into RepositoryManager

        return services;
    }
}
