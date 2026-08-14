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

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IAuthenticationService, IdentityAuthenticationService>();

        return builder;
    }
}
