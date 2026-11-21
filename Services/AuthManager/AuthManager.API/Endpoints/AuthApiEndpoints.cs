namespace AuthManager.API.Endpoints;

public static class AuthApiEndpoints
{
    public static void RegisterEndpoints(this IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapGroup("auth").WithTags("Authentication")
            .MapPost("register", (HttpContext http) =>
            {
                http.Response.Headers.CacheControl = "no-store";
                return Results.StatusCode(StatusCodes.Status410Gone);
            })
            .WithMetadata(new RetiredRegistration())
            .AllowAnonymous()
            .Produces(StatusCodes.Status410Gone);
    }

    public static void UseAuthHttpsRedirection(this WebApplication app)
    {
        // Retired registration must never redirect a submitted request body.
        app.UseWhen(context => context.GetEndpoint()?.Metadata.GetMetadata<RetiredRegistration>() is null,
            branch => branch.UseHttpsRedirection());
    }

    private sealed class RetiredRegistration;
}
