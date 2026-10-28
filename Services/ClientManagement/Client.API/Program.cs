using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ClientManagement.API.Services;
using Zeka.Extensions.MultiTenancy.AspNetCore;
using ClientManagement.API;
using ClientManagement.Application;
using ClientManagement.Application.SocialWorker.IntegrationEvents;
using ClientManagement.Application.SocialWorker.IntegrationEvents.EventHandlers;
using ClientManagement.Infrastructure;
using FluentAssertions.Common;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.RabbitMq;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddUserSecrets<Program>();
}

builder.Services.AddTenantAuthentication(builder.Configuration);
builder.Services.AddTenantContext();
builder.Services.AddScoped<TenantRequestIdentity>();
builder.Services.AddScoped<ClientManagement.Application.Common.Authorization.IOperationIdentity>(sp => sp.GetRequiredService<TenantRequestIdentity>());
builder.Services.AddScoped<ClientManagement.Infrastructure.Authorization.ITenantAccessCredential>(sp => sp.GetRequiredService<TenantRequestIdentity>());
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebServices(builder.Configuration);
builder.Services.AddRabbitMqEventBus(builder.Configuration)
    .AddRabbitMqSubscriberService(builder.Configuration)
    .AddEventHandler<SocialWorkerCreatedEvent, SocialWorkerCreatedEventHandler>()
    .AddEventHandler<Zeka.Contracts.Staff.V1.StaffProjectionChangedV1, ClientManagement.Infrastructure.Messaging.StaffProjectionConsumer>();

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
