using System.Net;
using System.Net.Http.Headers;
using ClientManagement.Application.Common.Authorization;
using Zeka.WorkerCredentials;

namespace ClientManagement.Infrastructure.Authorization;

public sealed class StaffMembershipClient(HttpClient client, ITenantAccessCredential credential) : IStaffMembershipLink
{
    public async Task<bool> VerifyAsync(Guid organisationId, Guid membershipId, bool requireActive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credential.BearerToken)) throw new TenantAccessException(AccessFailure.Unauthenticated);
        if (organisationId == Guid.Empty || membershipId == Guid.Empty) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"api/v1/tenant-access/{organisationId:D}/memberships/{membershipId:D}?includeInactive={!requireActive}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
                using var response = await client.SendAsync(request, deadline.Token);
                if (response.StatusCode == HttpStatusCode.NoContent) return true;
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    if (attempt == 1 && credential is IWorkerCredential401Retry worker
                        && await worker.ReloadAfterUnauthorizedAsync(deadline.Token))
                        continue;
                    throw new TenantAccessException(credential is IWorkerCredential401Retry
                        ? AccessFailure.Unavailable : AccessFailure.Unauthenticated);
                }
                if (response.StatusCode == HttpStatusCode.Forbidden) return false;
                throw new TenantAccessException(AccessFailure.Unavailable);
            }
            throw new TenantAccessException(AccessFailure.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TenantAccessException(AccessFailure.Unavailable); }
        catch (HttpRequestException) { throw new TenantAccessException(AccessFailure.Unavailable); }
        catch (WorkerCredentialUnavailableException) { throw new TenantAccessException(AccessFailure.Unavailable); }
    }
}
