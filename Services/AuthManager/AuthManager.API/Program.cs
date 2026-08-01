using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application;
using AuthManager.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Infrastructure(builder.Configuration);
builder.Services.Application();

builder.Services.RegisterTokenService(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddAutoMapper(typeof(Program));

builder.ConfigureMicrosoftIdentity();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
//    app.MigrateDatabase();
//}

app.RegisterEndpoints();

app.UseHttpsRedirection();


await app.AddRoles();

app.Run();
