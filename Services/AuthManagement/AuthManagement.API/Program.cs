using AuthManagement;
using AuthManagement.Endpoints;
using AuthManagement.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Infrastructure(builder.Configuration);

builder.Services.RegisterTokenService(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MigrateDatabase();
}

app.RegisterEndpoints();

app.UseHttpsRedirection();

app.Run();
