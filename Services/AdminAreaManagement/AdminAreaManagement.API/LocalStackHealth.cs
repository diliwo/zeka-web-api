using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AdminAreaManagement.API;

/// <summary>LocalStack-only status probes, before the business authentication pipeline.</summary>
public static class LocalStackHealth
{
    public static void Add(WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsEnvironment("LocalStack")) return;
        builder.Services.AddHealthChecks();
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
}
