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


var app = builder.Build();

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
