using AuthManager.Application.Common.Interfaces;
using AuthManager.Application.Common.Services;
using AuthManager.Application.Authorization;
using AuthManager.Application.Lifecycle;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace AuthManager.Application
{
    public static class DependencyInjection
    {
        public static void Application(this IServiceCollection services)
        {
            services.AddScoped<IServiceManager, ServiceManager>();
            services.AddScoped<MembershipPermissionGrants>();
            services.AddScoped<LifecycleExportCoordinator>();
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
        }
    }
}
