using System.Security.Claims;
using AuthManager.Application.Authentication;
using AuthManager.Application.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace AuthManager.API.Endpoints;

public static class WorkerRotationEndpoints
{
    public static void MapWorkerRotation(this WebApplication app)
    {
        // Mapping itself is closed outside the explicit isolated M1 environment.
        if (!app.Environment.IsEnvironment("LocalStack") || !app.Configuration.GetValue<bool>("M1WorkerRotation:Enabled")) return;
        foreach (var phase in new[] { "prepare", "begin-cutover", "abort", "inspect" })
        {
            var selected = phase;
            app.MapPost("/internal/m1/worker-rotation/" + phase, (WorkerRotationRequest request,
                HttpContext http, WorkerRotationService service, IBrowserSessions sessions) =>
                    ExecuteAsync(selected, request, null, http, service, sessions))
                .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
        }
        app.MapPost("/internal/m1/worker-rotation/finalize", (WorkerFinalizeRequest request,
            HttpContext http, WorkerRotationService service, IBrowserSessions sessions) =>
                ExecuteAsync("finalize", new(request.OperationId, request.OrganisationId), request.Witnesses, http, service, sessions))
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme });
    }

    private static async Task<IResult> ExecuteAsync(string phase, WorkerRotationRequest request,
        IReadOnlyList<WorkerUseWitness>? witnesses, HttpContext http, WorkerRotationService service, IBrowserSessions sessions)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        if (!http.Request.IsHttps || !TryCaller(http.User, out var caller)) return Results.Unauthorized();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            var active = await sessions.CheckActiveAsync(caller.SubjectId, caller.SessionId, deadline.Token);
            if (active == BrowserSessionOutcome.Unavailable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (active != BrowserSessionOutcome.Success) return Results.Unauthorized();
            return Results.Json(await service.ExecuteAsync(phase, caller, request, witnesses, deadline.Token));
        }
        catch (WorkerRotationException error)
        {
            var status = error.RequiresInspection ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            // Only constant safe codes cross the transport boundary. No request, provider or private response logging.
            return Results.Json(new { state = error.RequiresInspection ? "RECOVERY_REQUIRED" : "CONFLICT", code = error.Code }, statusCode: status);
        }
        catch (OperationCanceledException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        catch { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }

    public static bool TryCaller(ClaimsPrincipal principal, out WorkerRotationCaller caller)
    {
        caller = new(Guid.Empty, Guid.Empty);
        var claims = principal.Identities.Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims).ToArray();
        var subjects = claims.Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub").Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        var sessions = claims.Where(claim => claim.Type == "sid").Select(claim => claim.Value).ToArray();
        if (subjects.Length != 1 || sessions.Length != 1 || !Guid.TryParse(subjects[0], out var subject) || subject == Guid.Empty
            || !Guid.TryParse(sessions[0], out var session) || session == Guid.Empty) return false;
        caller = new(subject, session);
        return true;
    }
}
