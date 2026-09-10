using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application;
using AuthManager.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Infrastructure(builder.Configuration);
builder.Services.Application();

builder.Services.AddProblemDetails();

builder.ConfigureMicrosoftIdentity();

builder.Services.AddTenantAuthentication(builder.Configuration);
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapTenantAccess();

//if (app.Environment.IsDevelopment())
//{
//    app.MigrateDatabase();
//}

app.RegisterEndpoints();

app.UseHttpsRedirection();


app.Run();
