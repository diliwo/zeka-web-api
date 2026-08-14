using AuthManager.API.Endpoints;
using AuthManager.Application;
using AuthManager.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Infrastructure(builder.Configuration);
builder.Services.Application();

builder.Services.AddProblemDetails();

builder.ConfigureMicrosoftIdentity();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
//    app.MigrateDatabase();
//}

app.RegisterEndpoints();

app.UseHttpsRedirection();


app.Run();
