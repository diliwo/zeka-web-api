using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using AdminAreaManagement.API;
using AdminAreaManagement.API.Services;
using Zeka.Extensions.MultiTenancy.AspNetCore;
using AdminAreaManagement.Application;
using AdminAreaManagement.Infrastructure;
using AdminAreaManagement.Infrastructure.Persistence;
using Zeka.Extensions.EventBus.RabbitMq;
using Zeka.Extensions.Observability;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

builder.Services.AddTenantContext();
builder.Services.AddScoped<TenantRequestIdentity>();
builder.Services.AddScoped<AdminAreaManagement.Application.Common.Authorization.IOperationIdentity>(sp => sp.GetRequiredService<TenantRequestIdentity>());
builder.Services.AddScoped<AdminAreaManagement.Infrastructure.Authorization.ITenantAccessCredential>(sp => sp.GetRequiredService<TenantRequestIdentity>());
builder.Services.AddTenantAuthentication(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebServices();

builder.Services.AddRabbitMqEventBus(builder.Configuration)
    .AddRabbitMqEventPublisher();

//builder.Services.AddOpenTelemetryTracing("Adminapi", (traceBuiler) => traceBuiler.WithSqlInstrumentation());
builder.Services.AddControllers();


if (builder.Environment.IsEnvironment("LocalStack")) builder.Services.AddHealthChecks();
var app = builder.Build();
if (app.Environment.IsEnvironment("LocalStack"))
{
    // Internal status-only probes run before business authentication/transport middleware.
    app.UseHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate = _ => false,
        ResponseWriter = (_, _) => Task.CompletedTask
    });
    app.UseHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate = registration => registration.Tags.Contains("ready"),
        ResultStatusCodes = { [HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable },
        ResponseWriter = (_, _) => Task.CompletedTask
    });
}

//app.UseMigrationsAndSeed();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("CorsPolicy");
app.UseAuthentication();
app.UseMiddleware<TenantAccessMiddleware>();
app.UseAuthorization();

app.MapControllers();

app.Run();
