using System.Reflection;
using AdminAreaManagement.Application.Common.Behaviours;
using AdminAreaManagement.Application.Common.Helpers;
using AdminAreaManagement.Application.Common.Services;
using AdminAreaManagement.Application.Formations.Common;
using AdminAreaManagement.Application.Partners.Queries.Common;
using AdminAreaManagement.Application.Professions.Queries;
using AdminAreaManagement.Application.Schools.Common;
using AdminAreaManagement.Application.Services.Queries;
using AdminAreaManagement.Application.Staffs.Queries;
using AdminAreaManagement.Application.TrainingFields.Common;
using AdminAreaManagement.Application.TrainingTypes.Common;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AdminAreaManagement.Application
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddApplication(this IServiceCollection services)
        {
            services.AddTransient<IFileService, FileService>();
            services.AddScoped<ISortHelper<TrainingTypeDto>, SortHelper<TrainingTypeDto>>();
            services.AddScoped<ISortHelper<TrainingFieldDto>, SortHelper<TrainingFieldDto>>();
            services.AddScoped<ISortHelper<StaffMemberDto>, SortHelper<StaffMemberDto>>();
            services.AddScoped<ISortHelper<TeamDto>, SortHelper<TeamDto>>();
            services.AddScoped<ISortHelper<TrainingDto>, SortHelper<TrainingDto>>();
            services.AddScoped<ISortHelper<SchoolDto>, SortHelper<SchoolDto>>();
            services.AddScoped<ISortHelper<ProfessionDto>, SortHelper<ProfessionDto>>();
            services.AddScoped<ISortHelper<PartnerDto>, SortHelper<PartnerDto>>(); 
            services.AddScoped<ISortHelper<PartnerSelectionListDto>, SortHelper<PartnerSelectionListDto>>();
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
