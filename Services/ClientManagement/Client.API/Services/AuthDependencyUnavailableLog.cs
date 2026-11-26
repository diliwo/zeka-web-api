using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.API.Services;

internal static class AuthDependencyUnavailableLog
{
    private static readonly object Written = new();

    internal static void Write(HttpContext http, string requestCorrelationId, Func<ILogger> getLogger)
    {
        TenantAccessDiagnostics.Observe(() =>
        {
            if (!http.Items.TryAdd(Written, true)) return;

            getLogger().LogWarning(new EventId(1702, TenantAccessDiagnostics.DependencyUnavailable),
                "Auth dependency unavailable event_code={event_code} service={service} request_correlation_id={request_correlation_id}",
                TenantAccessDiagnostics.DependencyUnavailable, "ClientManagement", requestCorrelationId);
        });
    }
}
