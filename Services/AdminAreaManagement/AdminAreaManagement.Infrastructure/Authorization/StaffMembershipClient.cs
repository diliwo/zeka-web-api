using System.Net;
using System.Net.Http.Headers;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Staffs;

namespace AdminAreaManagement.Infrastructure.Authorization;

public sealed class StaffMembershipClient(HttpClient client, ITenantAccessCredential credential) : IStaffMembershipLink
{
    public async Task<bool> IsActiveAsync(Guid organisationId, Guid membershipId, CancellationToken cancellationToken)
    {
        if (organisationId == Guid.Empty || membershipId == Guid.Empty || string.IsNullOrWhiteSpace(credential.BearerToken))
            return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/v1/tenant-access/{organisationId:D}/memberships/{membershipId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        try
        {
            using var response = await client.SendAsync(request, deadline.Token);
            if (response.StatusCode == HttpStatusCode.NoContent) return true;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return false;
            throw new TenantAccessException(AccessFailure.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TenantAccessException(AccessFailure.Unavailable); }
        catch (HttpRequestException) { throw new TenantAccessException(AccessFailure.Unavailable); }
    }
}
