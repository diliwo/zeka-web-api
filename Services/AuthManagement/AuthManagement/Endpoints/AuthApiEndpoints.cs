using AuthManagement.Infrastructure.Interfaces;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.AspNetCore.Mvc;

namespace AuthManagement.Endpoints;

public static class AuthApiEndpoints
{
    public static void RegisterEndpoints(this IEndpointRouteBuilder routeBuilder)
    {
        routeBuilder.MapPost("/login", async Task<IResult> ([FromServices] ITokenService tokenService, LoginRequest loginRequest) =>
        {
            var loginResult = await tokenService.GenerateAuthenticationToken(loginRequest.Email, loginRequest.Password);

            return loginResult is null ? TypedResults.Unauthorized() : TypedResults.Ok(loginResult);
        });
    }
}