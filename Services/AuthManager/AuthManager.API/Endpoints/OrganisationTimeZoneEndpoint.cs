using System.Data.Common;
using AuthManager.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace AuthManager.API.Endpoints;

public static class OrganisationTimeZoneEndpoint
{
    public static void MapOrganisationTimeZone(this WebApplication app)
    {
        app.MapPost("/internal/v1/organisation-time-zone", async (
            OrganisationTimeZoneRequest? request, IOrganisationTimeZone resolver, HttpContext http,
            TimeProvider clock) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (request is null || request.ContractVersion != 1
                || request.OrganisationId == Guid.Empty || request.ActorSubjectId == Guid.Empty
                || request.ActorMembershipId == Guid.Empty)
                return Results.BadRequest();
            try
            {
                var result = await resolver.ResolveAsync(request, http.RequestAborted);
                if (result is null) return Results.NotFound();
                var age = clock.GetUtcNow() - result.ObservedAtUtc;
                if (result.OrganisationId != request.OrganisationId || result.ConfigVersion <= 0
                    || result.ObservedAtUtc.Offset != TimeSpan.Zero
                    || age < TimeSpan.FromSeconds(-30) || age > TimeSpan.FromSeconds(2))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                return Results.Ok(new
                {
                    contractVersion = 1, result.OrganisationId, result.TimeZoneId,
                    result.ConfigVersion, result.ObservedAtUtc
                });
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (DbException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization(new AuthorizeAttribute
        { AuthenticationSchemes = AssigneeServiceAuthentication.Scheme });
    }
}
