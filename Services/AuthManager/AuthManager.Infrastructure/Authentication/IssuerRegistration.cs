using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Zeka.Authentication;
namespace AuthManager.Infrastructure.Authentication;

public static class IssuerRegistration
{
    public static IServiceCollection AddAccessTokenIssuer(this IServiceCollection services, IConfiguration configuration)
    {
        var validation = AuthenticationOptions.Read(configuration);
        services.AddSingleton(IssuerOptions.Read(configuration, validation));
        services.TryAddSingleton(validation);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISigningKeyProvider, FileSigningKeyProvider>();
        services.AddSingleton<AccessTokenIssuer>();
        services.AddSingleton<IAccessTokenIssuer>(sp => sp.GetRequiredService<AccessTokenIssuer>());
        services.AddSingleton<IPublicJwksPublisher>(sp => sp.GetRequiredService<AccessTokenIssuer>());
        return services;
    }
}
