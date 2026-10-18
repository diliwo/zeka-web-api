using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Authentication;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Infrastructure(builder.Configuration);
builder.Services.Application();

builder.Services.AddProblemDetails();

builder.ConfigureMicrosoftIdentity();

builder.Services.AddTenantAuthentication(builder.Configuration);
builder.Services.AddAssigneeServiceAuthentication(builder.Configuration);
builder.Services.AddAccessTokenIssuer(builder.Configuration);
builder.Services.AddBrowserSessionTransport();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapBrowserSessions();
app.MapTenantAccess();
app.MapAssigneeEligibility();
app.MapOrganisationTimeZone();
app.MapDisabledConfigureTimeZone();
app.MapIssuerEndpoints();

//if (app.Environment.IsDevelopment())
//{
//    app.MigrateDatabase();
//}

app.RegisterEndpoints();

app.UseHttpsRedirection();


app.Run();
