using Zeka.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClientManagement.API;

public static class TenantAuthentication
{
    public static IServiceCollection AddTenantAuthentication(this IServiceCollection services, IConfiguration configuration)
        => services.AddZekaBearerValidation(configuration);
}
