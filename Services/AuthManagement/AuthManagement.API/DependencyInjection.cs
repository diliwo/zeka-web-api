using AuthManagement.Infrastructure.Interfaces;
using AuthManagement.Infrastructure.Persistence;
using AuthManagement.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Zeka.Extensions.Authentication;

namespace AuthManagement;

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

        services.AddScoped<IAuthStore, AuthDbContext>();
    }

    public static void RegisterTokenService(this IServiceCollection services, IConfigurationManager configuration)
    {
        var authOptions = new AuthOptions();
        configuration.GetSection(AuthOptions.AuthenticationSectionName).Bind(authOptions);
        services.AddSingleton(authOptions);

        services.AddScoped<ITokenService, JwtTokenService>();
    }
}