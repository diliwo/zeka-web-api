using System.Configuration;
using ClientManagement.API.Infrastructure;
using ClientManagement.Application;
using ClientManagement.Application.SchoolRegistations.Common;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Interfaces;
using ClientManagement.Infrastructure.Persistence.Helpers;
using ClientManagement.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
#if (UseApiOnly)
using NSwag;
using NSwag.Generation.Processors.Security;
#endif

namespace ClientManagement.API;

public static class DependencyInjection
{
    public static IServiceCollection AddWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDatabaseDeveloperPageExceptionFilter();

        //services.AddScoped<IUser, CurrentUser>();

        var fluidConfigSection = configuration.GetSection("OrganizationInfos");
        fluidConfigSection["AssessmentTemplateFilePath"] = Path.Combine("Assets", "Templates", "AssessmentReport.liquid");
        fluidConfigSection["LogoFilePath"] = Path.Combine("Assets", "Images", "Zekalogo.png");
        fluidConfigSection["Name"] = $"{fluidConfigSection["Name"]}";
        fluidConfigSection["ZipCode"] = $"{fluidConfigSection["ZipCode"]}";
        fluidConfigSection["Adress"] = $"{fluidConfigSection["Adress"]}";
        fluidConfigSection["PhoneNumer"] = $"{fluidConfigSection["PhoneNumer"]}";
        fluidConfigSection["Email"] = $"{fluidConfigSection["Email"]}";

        services.RegisterFluidProvider(fluidConfigSection);

        services.AddHttpContextAccessor();
        //services.AddExceptionHandler<CustomExceptionHandler>();

        // Customise default API behaviour
        services.Configure<ApiBehaviorOptions>(options =>
            options.SuppressModelStateInvalidFilter = true);

        services.AddEndpointsApiExplorer();

        services.AddSwaggerGen(c => { c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Client.API", Version = "v1" }); });

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
        services.AddControllers().AddNewtonsoftJson(options =>
            options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);


        return services;
    }
}
