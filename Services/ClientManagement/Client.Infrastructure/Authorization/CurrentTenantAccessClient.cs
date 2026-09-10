using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Infrastructure.Authorization;

// A delivery adapter supplies its validated credential. Worker hosts use a fresh adapter per operation.
public interface ITenantAccessCredential { string? BearerToken { get; } }

public sealed class CurrentTenantAccessClient(HttpClient client, ITenantAccessCredential credential) : ICurrentTenantAccess
{
    public async Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credential.BearerToken))
            return new(TenantAccessOutcome.Denied);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/v1/tenant-access/{selectedOrganisationId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.BearerToken);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new(TenantAccessOutcome.Denied);
            if (!response.IsSuccessStatusCode) return new(TenantAccessOutcome.Unavailable);
            var wire = await response.Content.ReadFromJsonAsync<AccessResponse>(cancellationToken: deadline.Token);
            if (wire is null || wire.ContractVersion != 1 || wire.SubjectId != authenticatedSubjectId
                || wire.OrganisationId != selectedOrganisationId || wire.OrganisationMembershipId == Guid.Empty
                || wire.EffectivePermissionCodes is null || wire.EffectivePermissionCodes.Any(p => !PermissionCatalogue.IsKnown(p)))
                return new(TenantAccessOutcome.Denied);
            return new(TenantAccessOutcome.Authorized, new(wire.OrganisationId, wire.OrganisationMembershipId,
                wire.EffectivePermissionCodes, wire.DecisionVersion, wire.ObservedAtUtc));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(TenantAccessOutcome.Unavailable); }
        catch (HttpRequestException) { return new(TenantAccessOutcome.Unavailable); }
        catch (JsonException) { return new(TenantAccessOutcome.Unavailable); }
    }

    private sealed record AccessResponse(int ContractVersion, string SubjectId, Guid OrganisationId,
        Guid OrganisationMembershipId, string[] EffectivePermissionCodes, string DecisionVersion,
        DateTimeOffset ObservedAtUtc);
}
