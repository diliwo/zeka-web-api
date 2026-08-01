using AuthManager.Application.Common.Interfaces;
using AuthManager.Application.Common.Mappings;
using AuthManager.Application.Common.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace AuthManager.Application
{
    public static class DependencyInjection
    {
        public static void Application(this IServiceCollection services)
        {
            services.AddScoped<IServiceManager, ServiceManager>();
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
            services.AddAutoMapper(typeof(UserMappingProfile).Assembly);
        }
    }
}
