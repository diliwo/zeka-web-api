using AuthManager.Infrastructure.Authentication;
using Zeka.Authentication;
namespace AuthManager.API.Endpoints;

public static class IssuerEndpoints
{
    public static void MapIssuerEndpoints(this WebApplication app)
    {
        app.MapGet("/.well-known/jwks.json", async (IPublicJwksPublisher publisher, HttpContext http) =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            budget.CancelAfter(AuthenticationOptions.RequestTimeout);
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var publication = await publisher.PrepareAsync(budget.Token);
                http.Response.OnCompleted(() => { if (http.Response.StatusCode == 200) publisher.ConfirmPublication(publication); return Task.CompletedTask; });
                return Results.Content(publication.Json, "application/json");
            }
            catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).AllowAnonymous();
        app.MapGet("/health/authentication", async (IAccessTokenIssuer issuer, HttpContext http) =>
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            budget.CancelAfter(AuthenticationOptions.RequestTimeout);
            http.Response.Headers.CacheControl = "no-store";
            return await issuer.IsReadyAsync(budget.Token) ? Results.Ok() : Results.StatusCode(503);
        }).AllowAnonymous();
    }
}
