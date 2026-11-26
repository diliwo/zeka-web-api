using Zeka.WorkerCredentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Infrastructure.Authorization;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.Infrastructure;

public static class TenantRegistration
{
    public static IServiceCollection AddTenantEnforcement(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IProtectedWorkerCredentialReader, LinuxProtectedWorkerCredentialReader>();
        services.TryAddSingleton<IWorkerCredentialProvider>(sp => new ProtectedWorkerCredentialProvider(
            sp.GetRequiredService<IProtectedWorkerCredentialReader>(),
            WorkerCredentialOptions.Read(configuration, WorkerCredentialEncoding.AdminAreaProfile),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ProtectedWorkerCredentialProvider>>()));
        services.TryAddScoped<TenantContextScope>();
        services.TryAddScoped<ITenantContextAccessor>(sp => sp.GetRequiredService<TenantContextScope>());
        services.TryAddScoped<ITenantContextInitializer>(sp => sp.GetRequiredService<TenantContextScope>());
        services.AddScoped<AdminAreaManagement.Application.Staffs.IStaffProjectionOutbox, Messaging.StaffProjectionOutbox>();
        services.AddHostedService<Messaging.StaffProjectionRetryWorker>();
        services.AddHttpClient("TenantWorkerAccess").ConfigurePrimaryHttpMessageHandler(() =>
            new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddHttpClient<AdminAreaManagement.Application.Staffs.IStaffMembershipLink, StaffMembershipClient>(client =>
        {
            if (!Uri.TryCreate(configuration["TenantAuthorization:AuthManagementUrl"], UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps) throw new InvalidOperationException("An HTTPS AuthManagement URL is required.");
            client.BaseAddress = uri;
            client.Timeout = TimeSpan.FromSeconds(2);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
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
