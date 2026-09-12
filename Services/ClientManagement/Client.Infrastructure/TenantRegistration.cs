using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Infrastructure;

public static class TenantRegistration
{
    public static IServiceCollection AddTenantEnforcement(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<TenantContextScope>();
        services.TryAddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextScope>());
        services.TryAddScoped<ITenantContextInitializer>(sp => sp.GetRequiredService<TenantContextScope>());
        services.AddHttpClient("TenantWorkerAccess").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient<ICurrentTenantAccess, CurrentTenantAccessClient>(client =>
        {
            var address = configuration["TenantAuthorization:AuthManagementUrl"];
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
                throw new InvalidOperationException("TenantAuthorization:AuthManagementUrl must be an HTTPS service URL.");
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(2);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false
        });
        return services;
    }
}
