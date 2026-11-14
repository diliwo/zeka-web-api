using ClientManagement.Application.Common.Authorization;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.API.Services;

public sealed class TenantAccessMiddleware(RequestDelegate next, ILogger<TenantAccessMiddleware>? logger = null)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var requestCorrelationId = http.TraceIdentifier;
        try { await next(http); }
        catch (TenantAccessException exception)
        {
            http.Response.StatusCode = exception.Failure switch
            {
                AccessFailure.Unauthenticated => StatusCodes.Status401Unauthorized,
                AccessFailure.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status403Forbidden
            };
            if (exception.Failure == AccessFailure.Unavailable && exception.Reason == TenantAccessDiagnostics.DependencyUnavailable)
            {
                if (!http.Response.Headers.CacheControl.ToString()
                    .Split(',', StringSplitOptions.TrimEntries).Contains("no-store", StringComparer.OrdinalIgnoreCase))
                    http.Response.Headers.Append("Cache-Control", "no-store");

                if (logger is not null)
                    AuthDependencyUnavailableLog.Write(http, requestCorrelationId, () => logger);
            }
        }
        catch (TenantContextException)
        { http.Response.StatusCode = StatusCodes.Status403Forbidden; }
    }
}
