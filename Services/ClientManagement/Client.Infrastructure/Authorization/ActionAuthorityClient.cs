using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using Microsoft.Extensions.Configuration;

namespace ClientManagement.Infrastructure.Authorization;

// No production credential is provisioned by this issue. The closed action gate precedes this adapter.
public interface IActionAuthorityServiceCredential { string? BearerToken { get; } }

internal sealed class UnprovisionedActionAuthorityCredential : IActionAuthorityServiceCredential
{
    public string? BearerToken => null;
}

internal sealed class ActionAuthorityClient(IHttpClientFactory factory, IConfiguration configuration,
    IActionAuthorityServiceCredential credential, TimeProvider clock) : IActionAuthorityReads
{
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public async Task<ActionTimeZone> TimeZoneAsync(Guid organisationId, Guid actorSubjectId,
        Guid actorMembershipId, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint("organisation-time-zone");
        var token = Token();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var request = NewRequest(endpoint, token,
                new { contractVersion = 1, organisationId, actorSubjectId, actorMembershipId });
            using var response = await factory.CreateClient("ActionAuthority").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw Unavailable();
            var result = await response.Content.ReadFromJsonAsync<TimeZoneWire>(WireOptions, deadline.Token);
            if (result is null || result.ContractVersion != 1 || result.OrganisationId != organisationId
                || result.ConfigVersion <= 0 || result.ObservedAtUtc == default
                || !Fresh(result.ObservedAtUtc, clock.GetUtcNow())
                || !IanaZone(result.TimeZoneId, out var zone)) throw Unavailable();
            return new ActionTimeZone(result.TimeZoneId!, result.ConfigVersion,
                result.ObservedAtUtc, zone!);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (NotSupportedException) { throw Unavailable(); }
    }

    public async Task<AssigneeDecision?> EligibilityAsync(Guid organisationId, Guid actorSubjectId,
        Guid actorMembershipId, Guid targetMembershipId, CancellationToken cancellationToken)
    {
        var endpoint = Endpoint("assignee-eligibility");
        var token = Token();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var request = NewRequest(endpoint, token,
                    new { contractVersion = 1, organisationId, actorSubjectId, actorMembershipId, targetMembershipId });
                try
                {
                    using var response = await factory.CreateClient("ActionAuthority").SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        var result = await response.Content.ReadFromJsonAsync<EligibilityWire>(WireOptions, deadline.Token);
                        if (result is null || result.ContractVersion != 1 || result.Eligible is null)
                            throw Unavailable();
                        if (result.Eligible == false)
                        {
                            if (result.OrganisationId is not null || result.ActorMembershipId is not null
                                || result.TargetMembershipId is not null || result.TargetViewScope is not null
                                || result.DecisionVersion is not null || result.ObservedAtUtc is not null)
                                throw Unavailable();
                            return null;
                        }
                        if (result.OrganisationId != organisationId || result.ActorMembershipId != actorMembershipId
                            || result.TargetMembershipId != targetMembershipId
                            || result.TargetViewScope is not ("All" or "Assigned")
                            || string.IsNullOrWhiteSpace(result.DecisionVersion) || result.DecisionVersion.Length > 256
                            || result.ObservedAtUtc is not { } observed || !Fresh(observed, clock.GetUtcNow()))
                            throw Unavailable();
                        return new AssigneeDecision(result.TargetViewScope, result.DecisionVersion, observed);
                    }
                    if (!Transient(response.StatusCode) || attempt == 2) throw Unavailable();
                }
                catch (HttpRequestException) when (attempt == 1) { }
                if (attempt == 1) await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
            throw Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw Unavailable(); }
        catch (HttpRequestException) { throw Unavailable(); }
        catch (JsonException) { throw Unavailable(); }
        catch (NotSupportedException) { throw Unavailable(); }
    }

    private Uri Endpoint(string purpose)
    {
        var address = configuration["ActionAuthority:AuthManagementUrl"];
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)) throw Unavailable();
        return new Uri(uri, $"/internal/v1/{purpose}");
    }

    private string Token() => string.IsNullOrWhiteSpace(credential.BearerToken)
        ? throw Unavailable() : credential.BearerToken!;

    private static HttpRequestMessage NewRequest(Uri endpoint, string token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static bool Transient(HttpStatusCode code) => code is HttpStatusCode.RequestTimeout
        or HttpStatusCode.TooManyRequests || (int)code >= 500;

    private static bool Fresh(DateTimeOffset observed, DateTimeOffset now) =>
        observed.Offset == TimeSpan.Zero && now - observed is var age
        && age >= TimeSpan.FromSeconds(-30) && age <= TimeSpan.FromSeconds(2);

    private static bool IanaZone(string? id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(id) || !id.Contains('/', StringComparison.Ordinal)
            || id.Length > 128) return false;
        try
        {
            zone = TimeZoneInfo.GetSystemTimeZones().SingleOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Id, id));
            return zone is not null;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    private static TenantAccessException Unavailable() => new(AccessFailure.Unavailable);

    private sealed record TimeZoneWire(int ContractVersion, Guid OrganisationId, string? TimeZoneId,
        long ConfigVersion, DateTimeOffset ObservedAtUtc);

    private sealed record EligibilityWire(int ContractVersion, bool? Eligible, Guid? OrganisationId,
        Guid? ActorMembershipId, Guid? TargetMembershipId, string? TargetViewScope,
        string? DecisionVersion, DateTimeOffset? ObservedAtUtc);
}
