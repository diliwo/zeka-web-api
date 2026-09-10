using System.Security.Claims;
using AuthManager.Application.Authorization;

namespace AuthManager.API.Endpoints;

public static class TenantAccessEndpoint
{
    public static void MapTenantAccess(this WebApplication app)
    {
        app.MapGet("/api/v1/tenant-access/{organisationId:guid}/memberships/{membershipId:guid}", async (
            Guid organisationId, Guid membershipId, ClaimsPrincipal caller, ICurrentTenantAccess resolver,
            IActiveOrganisationMembership memberships, HttpContext http, bool? includeInactive) =>
        {
            var subject = AuthenticatedSubject(caller);
            if (!Guid.TryParse(subject, out var subjectId)) return Results.Unauthorized();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var decision = await resolver.ResolveAsync(subjectId, organisationId, deadline.Token);
                if (decision.Outcome == TenantAccessOutcome.Unavailable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                if (decision.Outcome != TenantAccessOutcome.Authorized
                    || !decision.EffectivePermissionCodes.Contains("TeamConfiguration.ManageStaffProfiles"))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                return await memberships.ExistsAsync(organisationId, membershipId, deadline.Token, requireActive: includeInactive != true)
                    ? Results.NoContent() : Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (System.Data.Common.DbException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        { AuthenticationSchemes = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme });
        app.MapGet("/api/v1/tenant-access/{organisationId:guid}", async (
            Guid organisationId, ClaimsPrincipal caller, ICurrentTenantAccess resolver, HttpContext http) =>
        {
            var subject = AuthenticatedSubject(caller);
            if (!Guid.TryParse(subject, out var subjectId)) return Results.Unauthorized();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var result = await resolver.ResolveAsync(subjectId, organisationId, deadline.Token);
                if (result.Outcome == TenantAccessOutcome.Unavailable) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                return result.Outcome == TenantAccessOutcome.Authorized
                    ? Results.Ok(new { ContractVersion = 1, SubjectId = subject,
                        result.OrganisationId, result.OrganisationMembershipId,
                        result.EffectivePermissionCodes, result.DecisionVersion, result.ObservedAtUtc })
                    : Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (System.Data.Common.DbException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute
        { AuthenticationSchemes = Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme });
    }
    private static string? AuthenticatedSubject(ClaimsPrincipal caller)
    {
        var subjects = caller.Identities.Where(identity => identity.IsAuthenticated).SelectMany(identity => identity.Claims)
            .Where(claim => claim.Type is ClaimTypes.NameIdentifier or "sub")
            .Select(claim => claim.Value).Distinct(StringComparer.Ordinal).ToArray();
        return subjects.Length == 1 ? subjects[0] : null;
    }}
