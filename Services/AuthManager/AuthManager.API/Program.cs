using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
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
LocalStackHealth.Add(builder);
var app = builder.Build();
app.UseLocalStackHealth();
app.UseBrowserSessionAttribution();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapBrowserSessions();
app.MapTenantAccess();
app.MapBrowserEligibleOrganisations();
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

namespace AuthManager.API
{
    /// <summary>Local Compose probes; business transport and all other environments remain unchanged.</summary>
    public static class LocalStackHealth
    {
        public static void Add(WebApplicationBuilder builder)
        {
            if (!builder.Environment.IsEnvironment("LocalStack")) return;
            builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
                "authentication-issuer",
                services => new IssuerReadiness(services.GetRequiredService<IAccessTokenIssuer>()),
                HealthStatus.Unhealthy, ["ready"], TimeSpan.FromSeconds(2)));
        }

        public static void UseLocalStackHealth(this WebApplication app)
        {
            if (!app.Environment.IsEnvironment("LocalStack")) return;
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

        private sealed class IssuerReadiness(IAccessTokenIssuer issuer) : IHealthCheck
        {
            public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
                CancellationToken cancellationToken = default)
            {
                try
                {
                    return await issuer.IsReadyAsync(cancellationToken)
                        ? HealthCheckResult.Healthy()
                        : HealthCheckResult.Unhealthy("Authentication issuer unavailable.");
                }
                catch
                {
                    return HealthCheckResult.Unhealthy("Authentication issuer unavailable.");
                }
            }
        }
    }
}
