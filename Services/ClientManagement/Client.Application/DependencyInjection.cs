using System.Reflection;
using ClientManagement.Application.Common.Behaviours;
using ClientManagement.Application.Common.Helpers;
using ClientManagement.Application.Common.Interfaces;
using ClientManagement.Application.Common.Services;
using ClientManagement.Application.SchoolRegistations.Common;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using DinkToPdf;
using DinkToPdf.Contracts;
using System.Configuration;
using ClientManagement.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace ClientManagement.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddTransient<ISortHelper<MySupportDto>, SortHelper<MySupportDto>>();
            services.AddTransient<ISortHelper<SchoolRegistrationDto>, SortHelper<SchoolRegistrationDto>>();
            services.AddTransient<IDocumentGeneratorService, DocumentGeneratorService>();
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

        public static IServiceCollection RegisterFluidProvider(this IServiceCollection services, IConfigurationSection configurationSection)
        {
            services.Configure<FluidServiceConfiguration>(configurationSection.Bind);
            services.AddSingleton<IConverter>(sp => new SynchronizedConverter(new PdfTools()));
            return services;
        }
    }
}
