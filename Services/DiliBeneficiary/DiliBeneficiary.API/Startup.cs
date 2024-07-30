using DiliBeneficiary.API.Filters;
using DiliBeneficiary.API.Services;
using DiliBeneficiary.Application;
using DiliBeneficiary.Core.Interfaces;
using DiliBeneficiary.Infrastructure;
using DiliBeneficiary.Infrastructure.Persistence;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.OpenApi.Models;

namespace DiliBeneficiary.API;

public class Startup
{
    public IConfiguration Configuration { get; }
    private readonly IWebHostEnvironment HostingEnvironment;
    private string _fileServerPath => Configuration.GetValue<string>("FileServerPath");

    public Startup(IConfiguration configuration, IWebHostEnvironment hostingEnvironment)
    {
        Configuration = configuration;
        HostingEnvironment = hostingEnvironment;
    }

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddApplication();
        services.AddInfrastructure(Configuration);

        var fluidConfigSection = Configuration.GetSection("CpasInfos");
        fluidConfigSection["BilanTemplateFilePath"] = $"{HostingEnvironment.ContentRootPath}\\{fluidConfigSection["BilanTemplateFilePath"]}";
        fluidConfigSection["LogoCPASFilePath"] = $"{HostingEnvironment.ContentRootPath}\\{fluidConfigSection["LogoCPASFilePath"]}";
        fluidConfigSection["CpasNameFr"] = $"{fluidConfigSection["CpasNameFr"]}";
        fluidConfigSection["CpasNameNl"] = $"{fluidConfigSection["CpasNameNl"]}";
        fluidConfigSection["CpasZip"] = $"{fluidConfigSection["CpasZip"]}";
        fluidConfigSection["CpasAdresse"] = $"{fluidConfigSection["CpasAdresse"]}";
        fluidConfigSection["CallCenter"] = $"{fluidConfigSection["CallCenter"]}";
        fluidConfigSection["EmailFr"] = $"{fluidConfigSection["EmailFr"]}";
        fluidConfigSection["EmailNl"] = $"{fluidConfigSection["EmailNl"]}";

        //services.RegisterFluidProvider(fluidConfigSection);

        services.AddHealthChecks()
            .AddDbContextCheck<ApplicationDbContext>();

        services.AddControllersWithViews(options =>
            options.Filters.Add<ApiExceptionFilterAttribute>())
                .AddFluentValidation();

        services.AddControllers();
        services.AddControllers().AddNewtonsoftJson(options =>
            options.SerializerSettings.ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore);// For preventing reference loop issues

        services.AddCors(options =>
        {
            options.AddPolicy("AllowOrigin",
                builder => builder.AllowAnyOrigin());
        });

        services.AddSingleton(x => new FileRepositorySettings(_fileServerPath));

        // Customise default API behaviour
        services.Configure<ApiBehaviorOptions>(options =>
        {
            options.SuppressModelStateInvalidFilter = true;
        });


        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "ISP Api",
                Version = "v1",
                Description = "L'API PathFinder permet de gérer les parcours socio-professionnels.",
                Contact = new OpenApiContact
                    { Name = "Diligent Worker", Email = "diligent.worker@gmail.com" }
            });

            options.OperationFilter<AuthorizeCheckOperationFilter>();
            options.CustomSchemaIds(type => type.ToString());
        });
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
            app.UseMigrationsEndPoint();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            //app.UseHsts();
        }

        //app.UseMigrationsAndSeed();
        app.UseHealthChecks("/health");
        app.UseStaticFiles();
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Swagger Isp Api V1");
            c.OAuthClientId(Configuration.GetSection("Swagger")["ClientId"]);
        });
        app.UseRouting();
        //app.UseAuthentication();
        //app.UseAuthorization();

        // CORS setup must be set before MVC
        var section = Configuration.GetSection("CorsAllowOrigins");
        string[] origins = section.Get<string>().Split(",");
        if ((origins != null) && (origins.Length > 0))
        {
            app.UseCors(builder => builder
                .WithOrigins(origins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials());
        }
        app.UseEndpoints(endpoints => endpoints.MapControllers());
    }
}