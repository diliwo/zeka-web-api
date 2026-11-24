using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using AuthManager.Application.Provisioning;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthManager.API.Endpoints;

/// <summary>Browser delivery only; Auth's shared application service owns provisioning.</summary>
public static class BrowserOrganisationEndpoints
{
    private static readonly AuthorizeAttribute Bearer = new()
        { AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme };
    private const int MaximumBodyBytes = 4096;

    public static void MapBrowserOrganisationCreation(this RouteGroupBuilder sessions)
        => sessions.MapPost("/organisations", CreateAsync).RequireAuthorization(Bearer);

    public static void MapBrowserOrganisationAccess(this WebApplication app)
        => app.MapGet("/api/v1/browser-organisations/{organisationId:guid}/access", AccessAsync)
            .RequireAuthorization(Bearer);

    private static async Task<IResult> CreateAsync(HttpContext http, [FromServices] IBrowserOrganisationCreation creation)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryReadIdentity(http.User, out var subject, out var session)) return Results.Unauthorized();
        if (http.Request.QueryString.HasValue || http.Request.Headers.ContainsKey("X-Organisation-Id"))
            return Invalid();
        var keys = http.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !Guid.TryParseExact(keys[0], "D", out var key) || key == Guid.Empty)
            return Invalid();
        var displayName = await DisplayNameAsync(http);
        if (displayName is null) return Invalid();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var result = await creation.CreateAsync(subject, session,
                new(displayName, null, key.ToString("D"), null), deadline.Token);
            if (result.SubjectId != subject) return Unavailable();
            return result.Outcome switch
            {
                OrganisationProvisioningOutcome.Created or OrganisationProvisioningOutcome.Replay
                    when result.OrganisationId != Guid.Empty && result.MembershipId != Guid.Empty
                    => Results.Json(new { ContractVersion = 1, SubjectId = subject,
                        result.OrganisationId, result.MembershipId }, statusCode: 201),
                OrganisationProvisioningOutcome.Conflict => Problem(409, "ORGANISATION_IDEMPOTENCY_CONFLICT"),
                OrganisationProvisioningOutcome.Processing => Problem(409, "ORGANISATION_CREATION_IN_PROGRESS"),
                _ => Unavailable()
            };
        }
        catch (OrganisationProvisioningException error)
        {
            return error.Code switch
            {
                "ORGANISATION_CREATION_UNAUTHENTICATED" => Results.Unauthorized(),
                "ORGANISATION_CREATION_REQUEST_INVALID" or "PROVISIONING_REQUEST_REJECTED" => Invalid(),
                "ORGANISATION_OWNERSHIP_LIMIT_REACHED" => Problem(409, error.Code),
                "ORGANISATION_CREATION_RESULT_UNCERTAIN" => Uncertain(),
                _ => Unavailable()
            };
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
        { return Uncertain(); }
        catch (DbException) { return Unavailable(); }
    }

    private static async Task<IResult> AccessAsync(Guid organisationId, HttpContext http,
        IBrowserSessions sessions, ICurrentTenantAccess access)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!TryReadIdentity(http.User, out var subject, out var session)) return Results.Unauthorized();
        if (http.Request.QueryString.HasValue || http.Request.Headers.ContainsKey("X-Organisation-Id"))
            return Invalid();
        if (organisationId == Guid.Empty) return Results.NotFound();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var active = await sessions.CheckActiveAsync(subject, session, deadline.Token);
            if (active == BrowserSessionOutcome.Unavailable) return Unavailable();
            if (active != BrowserSessionOutcome.Success) return Results.Unauthorized();
            var result = await access.ResolveAsync(subject, organisationId, deadline.Token);
            if (result.Outcome == TenantAccessOutcome.Unavailable) return Unavailable();
            // Unknown and another user's organisations have the same non-disclosing response.
            if (result.Outcome != TenantAccessOutcome.Authorized) return Results.NotFound();
            if (result.OrganisationId != organisationId || result.OrganisationMembershipId == Guid.Empty
                || string.IsNullOrWhiteSpace(result.DisplayName) || string.IsNullOrWhiteSpace(result.DecisionVersion))
                return Unavailable();
            return Results.Ok(new { ContractVersion = 1, SubjectId = subject, result.OrganisationId,
                MembershipId = result.OrganisationMembershipId, Permissions = result.EffectivePermissionCodes,
                AuthorizationVersion = result.DecisionVersion, result.ObservedAtUtc, result.DisplayName,
                result.TimeZoneId, result.TimeZoneConfigVersion });
        }
        catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested) { return Unavailable(); }
        catch (DbException) { return Unavailable(); }
    }

    internal static bool TryReadIdentity(ClaimsPrincipal caller, out Guid subject, out Guid session)
    {
        subject = session = Guid.Empty;
        var claims = caller.Identities.Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims).ToArray();
        var subjects = claims.Where(claim => claim.Type is "sub" or ClaimTypes.NameIdentifier).ToArray();
        var sessions = claims.Where(claim => claim.Type == "sid").ToArray();
        return subjects.Length == 1 && sessions.Length == 1
            && Guid.TryParse(subjects[0].Value, out subject) && subject != Guid.Empty
            && Guid.TryParse(sessions[0].Value, out session) && session != Guid.Empty;
    }

    private static async Task<string?> DisplayNameAsync(HttpContext http)
    {
        if (http.Request.ContentLength > MaximumBodyBytes) return null;
        var bytes = new byte[MaximumBodyBytes + 1];
        var used = 0;
        try
        {
            while (used < bytes.Length)
            {
                var read = await http.Request.Body.ReadAsync(bytes.AsMemory(used), http.RequestAborted);
                if (read == 0)
                {
                    using var document = JsonDocument.Parse(bytes.AsMemory(0, used), new() { MaxDepth = 2 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                    var properties = document.RootElement.EnumerateObject().ToArray();
                    if (properties.Length != 1 || properties[0].Name != "displayName"
                        || properties[0].Value.ValueKind != JsonValueKind.String) return null;
                    var value = properties[0].Value.GetString()?.Trim();
                    return string.IsNullOrWhiteSpace(value) || value.Length > 200 ? null : value;
                }
                used += read;
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { }
        return null;
    }

    private static IResult Invalid() => Problem(400, "ORGANISATION_CREATION_REQUEST_INVALID");
    private static IResult Unavailable() => Problem(503, "AUTH_DEPENDENCY_UNAVAILABLE");
    private static IResult Uncertain() => Problem(503, "ORGANISATION_CREATION_RESULT_UNCERTAIN");
    private static IResult Problem(int status, string code) => Results.Problem(statusCode: status,
        title: "The organisation request could not be completed.", extensions: new Dictionary<string, object?> { ["code"] = code });
}
