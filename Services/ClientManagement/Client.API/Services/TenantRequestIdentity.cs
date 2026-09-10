using System.Security.Claims;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;

namespace ClientManagement.API.Services;

public sealed class TenantRequestIdentity(IHttpContextAccessor http) : IOperationIdentity, ITenantAccessCredential
{
    public string SubjectId
    {
        get
        {
            var identity = http.HttpContext?.User.Identities.FirstOrDefault(i => i.IsAuthenticated);
                        var subjects = identity?.Claims.Where(c => c.Type is ClaimTypes.NameIdentifier or "sub")
                .Select(c => c.Value).Distinct(StringComparer.Ordinal).ToArray();
            return subjects?.Length == 1 ? subjects[0] : string.Empty;
        }
    }
    public Guid SelectedOrganisationId
    {
        get
        {
            var context = http.HttpContext;
            var values = context?.Request.Headers["X-Organisation-Id"];
            if (values is null || values.Value.Count != 1
                || !Guid.TryParse(values.Value[0], out var id) || id == Guid.Empty) return Guid.Empty;
            var claims = context!.User.Identities.Where(i => i.IsAuthenticated)
                .SelectMany(i => i.Claims).Where(c => c.Type is "organisation_id" or "org_id");
            return claims.Any(c => !Guid.TryParse(c.Value, out var claimed) || claimed != id) ? Guid.Empty : id;
        }
    }
    public string? BearerToken
    {
        get
        {
            var values = http.HttpContext?.Request.Headers.Authorization;
            if (values is null || values.Value.Count != 1) return null;
            var header = values.Value[0];
            return header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
                ? header[7..] : null;
        }
    }
}
