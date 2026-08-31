using AuthManager.Infrastructure.CustomTokenProviders;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Infrastructure.Outbox;
using AuthManager.Infrastructure.Persistence.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuthManager.Infrastructure;

public static class DependencyInjection
{
    public static void Infrastructure(this IServiceCollection services, IConfigurationManager configuration)
    {
        services.AddDbContext<AuthDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString("Default"),
                npgsqlOptionsAction: npgsqlOptions =>
                {
                    npgsqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(40),
                        errorCodesToAdd: new List<string> { "0"});
                }));

        // to revert to the pre-6.0 behavior to avoid the timeZone mapping
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

        services.AddOnboardingPersistenceFoundations(configuration);

        if (configuration.GetValue<bool>($"{OutboxDispatcherOptions.SectionName}:Enabled"))
            services.AddHostedService<OutboxBackgroundService>();
    }

    public static IServiceCollection AddOnboardingPersistenceFoundations(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.TryAddScoped<IOutboxMessagePublisher, UnconfiguredOutboxMessagePublisher>();
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        services.Configure<OutboxDispatcherOptions>(
            configuration.GetSection(OutboxDispatcherOptions.SectionName));
        return services;
    }

    public static WebApplicationBuilder ConfigureMicrosoftIdentity(this WebApplicationBuilder builder)
    {
        builder.Services.AddIdentity<User, IdentityRole<Guid>>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Tokens.EmailConfirmationTokenProvider = "emailconfirmation";
            options.Lockout.MaxFailedAccessAttempts = 3;
        })
        .AddEntityFrameworkStores<AuthDbContext>()
        .AddDefaultTokenProviders()
        .AddTokenProvider<EmailConfirmationTokenProvider<User>>("emailconfirmation");

        builder.Services.Configure<DataProtectionTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromHours(2));

        builder.Services.Configure<EmailConfirmationTokenProviderOptions>(options =>
            options.TokenLifespan = TimeSpan.FromDays(3));

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IAuthenticationService, IdentityAuthenticationService>();

        return builder;
    }
}
