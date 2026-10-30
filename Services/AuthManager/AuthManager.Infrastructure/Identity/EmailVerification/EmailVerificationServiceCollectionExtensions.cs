using System.Globalization;
using AuthManager.Application.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AuthManager.Infrastructure.Identity.EmailVerification;

public static class EmailVerificationServiceCollectionExtensions
{
    // Explicit opt-in. No lifetime or delivery credential is inferred at startup.
    public static IServiceCollection AddEmailVerificationExchange(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(EmailVerificationExchangeOptions.SectionName);
        var entries = section.GetChildren().ToArray();
        if (entries.Length != 1
            || !string.Equals(entries[0].Key,
                nameof(EmailVerificationExchangeOptions.RestrictedGrantLifetime),
                StringComparison.OrdinalIgnoreCase)
            || !TimeSpan.TryParse(entries[0].Value, CultureInfo.InvariantCulture, out var lifetime)
            || lifetime != TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("AUTH_ONBOARDING_CONFIGURATION_REJECTED");

        services.Configure<EmailVerificationExchangeOptions>(options =>
            options.RestrictedGrantLifetime = lifetime);
        services.TryAddScoped<IEmailVerificationExchange, EmailVerificationExchange>();
        return services;
    }
}