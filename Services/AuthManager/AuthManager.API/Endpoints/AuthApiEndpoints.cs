using AuthManager.API.Filters;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Models.Users;
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
            [FromServices] IServiceManager serviceManager) =>
        {
            var result = await serviceManager.AuthenticationService
                .RegisterUserAsync(request);

            if (!result.Succeeded)
            {
                var errorsDict = new Dictionary<string, string[]>
                {
                    {
                        "user-validation errors",
                        result.Errors.Select(x => x.Description).ToArray()
                    }
                };

                return Results.ValidationProblem(errorsDict);
            }

            return Results.Created();
        })
        .AddEndpointFilter<ValidationFilter<RegisterUserRequest>>()
        .Produces(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .ProducesValidationProblem(StatusCodes.Status422UnprocessableEntity); ;

        //routeBuilder.MapPost("/login", async Task<IResult> ([FromServices] ITokenService tokenService, LoginRequest loginRequest) =>
        //{
        //    var loginResult = await tokenService.GenerateAuthenticationToken(loginRequest.Email, loginRequest.Password);

        //    return loginResult is null ? TypedResults.Unauthorized() : TypedResults.Ok(loginResult);
        //});
    }
}