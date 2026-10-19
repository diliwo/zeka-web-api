using System.Data.Common;
using System.Security.Claims;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace AuthManager.API.Endpoints;

public static class BrowserEligibleOrganisationsEndpoint
{
    public static void MapBrowserEligibleOrganisations(this WebApplication app)
    {
        app.MapGet("/api/v1/browser-organisations/eligible", async (ClaimsPrincipal caller,
            IBrowserSessions sessions, IEligibleOrganisations resolver, HttpContext http) =>
        {
            // Bearer challenges already set no-store in the existing authentication handler.
            http.Response.Headers.CacheControl = "no-store";
            var claims = caller.Identities.Where(identity => identity.IsAuthenticated)
                .SelectMany(identity => identity.Claims).ToArray();
            var subjects = claims.Where(claim => claim.Type is "sub" or ClaimTypes.NameIdentifier).ToArray();
            var sessionIds = claims.Where(claim => claim.Type == "sid").ToArray();
            if (subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var subject) || subject == Guid.Empty
                || sessionIds.Length != 1 || !Guid.TryParse(sessionIds[0].Value, out var session) || session == Guid.Empty)
                return Results.Unauthorized();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var active = await sessions.CheckActiveAsync(subject, session, deadline.Token);
                if (active == BrowserSessionOutcome.Unavailable) return Results.StatusCode(503);
                if (active != BrowserSessionOutcome.Success) return Results.Unauthorized();
                // This endpoint accepts no query input. Even an empty selector cannot establish tenant context.
                if (http.Request.Headers.ContainsKey("X-Organisation-Id") || http.Request.QueryString.HasValue)
                    return Results.BadRequest();
                var result = await resolver.ResolveAsync(subject, deadline.Token);
                return result.Outcome switch
                {
                    EligibleOrganisationsOutcome.Success => Results.Ok(new
                        { ContractVersion = 1, SubjectId = subject, result.Organisations }),
                    EligibleOrganisationsOutcome.Unauthenticated => Results.Unauthorized(),
                    EligibleOrganisationsOutcome.Overflow => Results.Conflict(),
                    _ => Results.StatusCode(503)
                };
            }
            catch (OperationCanceledException) { return Results.StatusCode(503); }
            catch (DbException) { return Results.StatusCode(503); }
        }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
    }
}
