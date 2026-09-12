using AdminAreaManagement.Application.Common.Authorization;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.API.Services;

public sealed class TenantAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http)
    {
        try { await next(http); }
        catch (TenantAccessException exception)
        {
            http.Response.StatusCode = exception.Failure switch
            {
                AccessFailure.Unauthenticated => StatusCodes.Status401Unauthorized,
                AccessFailure.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status403Forbidden
            };
        }
        catch (TenantContextException)
        { http.Response.StatusCode = StatusCodes.Status403Forbidden; }
    }
}
