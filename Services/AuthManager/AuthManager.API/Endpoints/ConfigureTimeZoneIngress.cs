namespace AuthManager.API.Endpoints;

/// <summary>
/// Deliberately non-activatable until the separate human, key-custody and database
/// writer trust controls are approved. No service or user bearer can execute a write.
/// </summary>
public static class ConfigureTimeZoneIngress
{
    public static void MapDisabledConfigureTimeZone(this WebApplication app)
    {
        if (app.Configuration.GetSection("ConfigureTimeZoneIngress").GetChildren().Any())
            throw new InvalidOperationException(
                "ConfigureTimeZone ingress cannot be enabled by application configuration.");
        app.MapPost("/internal/v1/configure-time-zone", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.NotFound();
        }).ExcludeFromDescription();
    }
}
