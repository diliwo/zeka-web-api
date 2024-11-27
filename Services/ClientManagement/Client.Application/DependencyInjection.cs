using System.Reflection;
using Client.Application.Common.Behaviours;
using Client.Application.Common.Services;
using Client.Core.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Client.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddAutoMapper(Assembly.GetExecutingAssembly());
            services.AddValidatorsFromAssembly(Assembly.GetExecutingAssembly());
            services.AddMediatR(cfg => {
                cfg.RegisterServicesFromAssembly(Assembly.GetExecutingAssembly());
                cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(UnhandledExceptionBehaviour<,>));
                //cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(AuthorizationBehaviour<,>));
                cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehaviour<,>));
                cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(PerformanceBehaviour<,>));
            });
            services.AddScoped<IDomainEventService, DomainEventService>();
            //services.AddTransient<IDocumentGeneratorService, DocumentGeneratorService>();
            
            return services;
        }
    }
}
