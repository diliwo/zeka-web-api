using ClientManagement.API;
using ClientManagement.Application;
using ClientManagement.Application.SocialWorker.IntegrationEvents;
using ClientManagement.Application.SocialWorker.IntegrationEvents.EventHandlers;
using ClientManagement.Infrastructure;
using FluentAssertions.Common;
using Zeka.Extensions.Authentication;
using Zeka.Extensions.EventBus;
using Zeka.Extensions.EventBus.RabbitMq;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddJwtAuthentication(builder.Configuration);
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddWebServices(builder.Configuration);
builder.Services.AddRabbitMqEventBus(builder.Configuration)
    .AddRabbitMqSubscriberService(builder.Configuration)
    .AddEventHandler<SocialWorkerCreatedEvent, SocialWorkerCreatedEventHandler>();

builder.Services.AddControllers();


var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseCors("CorsPolicy");
app.UseJwtAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
