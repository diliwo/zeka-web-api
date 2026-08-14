using AuthManager.API.Filters;
using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace AuthManager.API.Endpoints;

public static class AuthApiEndpoints
{
    public static void RegisterEndpoints(this IEndpointRouteBuilder routeBuilder)
    {
        // Helpful for the API documentation.
        var group = routeBuilder.MapGroup("auth")
        .WithTags("Authentication");

        group.MapPost("register", async (
            [FromBody] RegisterUserRequest request,
            [FromServices] IServiceManager serviceManager,
            CancellationToken cancellationToken) =>
        {
            var result = await serviceManager.AuthenticationService
                .RegisterUserAsync(request, cancellationToken);

            if (!result.Succeeded)
            {
                var errorsDict = new Dictionary<string, string[]>
                {
                    {
                        "user-validation errors",
                        result.Errors.ToArray()
                    }
                };

                return Results.ValidationProblem(errorsDict);
            }

            return Results.Created();
        })
        .AddEndpointFilter<ValidationFilter<RegisterUserRequest>>()
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity);
    }
}
