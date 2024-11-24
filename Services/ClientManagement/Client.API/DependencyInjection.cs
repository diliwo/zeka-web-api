using System.Reflection;
using Client.API.Infrastructure;
using Client.Application.SchoolRegistations.Common;
using Client.Infrastructure.Persistence;
using Client.Infrastructure.Persistence.Helpers;
using Client.Infrastructure.Services;
using Client.Core.Common.Dto;
using Client.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
#if (UseApiOnly)
using NSwag;
using NSwag.Generation.Processors.Security;
#endif

namespace Client.API;

public static class DependencyInjection
{
    public static IServiceCollection AddWebServices(this IServiceCollection services)
    {
        services.AddDatabaseDeveloperPageExceptionFilter();

        //services.AddScoped<IUser, CurrentUser>();

        services.AddHttpContextAccessor();

        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        services.AddExceptionHandler<CustomExceptionHandler>();
        services.AddTransient<ISortHelper<MyConsultantSupportDto>, SortHelper<MyConsultantSupportDto>>();
        services.AddTransient<ISortHelper<SchoolRegistrationDto>, SortHelper<SchoolRegistrationDto>>();
        services.AddScoped<ISortHelper<MyJobCoachSupportDto>, SortHelper<MyJobCoachSupportDto>>();
        services.AddScoped<IClientService, ClientService>();

        // Customise default API behaviour
        services.Configure<ApiBehaviorOptions>(options =>
            options.SuppressModelStateInvalidFilter = true);

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(c => { c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Catalog.API", Version = "v1" }); });

        //Register AutoMapper
        services.AddAutoMapper(typeof(Program).Assembly);

        //Add Cors
        services.AddCors(options =>
        {
            options.AddPolicy("CorsPolicy", policy =>
            {
                policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin();
            });
        });


        services.AddControllers();

        //Register AutoMapper
        services.AddAutoMapper(typeof(Program).Assembly);
        return services;
    }
}
