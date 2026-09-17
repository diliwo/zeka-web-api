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
using AdminAreaManagement.Application.Common.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeka.PersistenceSecurity;

namespace AdminAreaManagement.Infrastructure.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTenantEnforcement(configuration);
        services.RemoveAll<IFileService>();
        services.AddSingleton<IFileService>(_ => new FileService(new FileRepositorySettings(
            configuration.GetValue<string>("FileServerPath")
            ?? throw new InvalidOperationException("FileServerPath is required."))));

        services.TryAddSingleton<ITenantAttemptOrderObserver, NullTenantAttemptOrderObserver>();
        var runtimeConnection = configuration.GetConnectionString("ClientApiConnection");
        if (string.IsNullOrWhiteSpace(runtimeConnection))
            throw new InvalidOperationException("ConnectionStrings:ClientApiConnection is required.");
        services.AddScoped<TenantTransactionAttemptState>();
        services.AddScoped<TenantPostCommitActions>();
        services.AddScoped<TenantCommandGuard>();
        services.AddScoped<ITenantTransactionExecutor, TenantTransactionExecutor>();
        services.AddDbContext<ApplicationDbContext>((provider, options) =>
            options.UseNpgsql(
                runtimeConnection,
                b => b.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName)
                    .EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(200), null))
                .AddInterceptors(provider.GetRequiredService<TenantCommandGuard>()));

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

        // Runtime health must not issue an unscoped command through the tenant DbContext.
        services.AddHealthChecks().AddCheck(
            "adminarea-database-connectivity",
            new DatabaseConnectivityHealthCheck(runtimeConnection));

        return services;
    }
}
