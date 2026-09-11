using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;

namespace ClientManagement.Infrastructure.Authorization;

public interface ITenantAccessCredential { string? BearerToken { get; } }

public sealed class CurrentTenantAccessClient(HttpClient client, ITenantAccessCredential credential) : ICurrentTenantAccess
{
    public const string DiagnosticsName = "ClientManagement.TenantAccess";
    private static readonly ActivitySource Traces = new(DiagnosticsName);
    private static readonly Meter Metrics = new(DiagnosticsName);
    private static readonly Counter<long> Attempts = Metrics.CreateCounter<long>("tenant_access.attempts");
    private static readonly Counter<long> Failures = Metrics.CreateCounter<long>("tenant_access.authority_failures");
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>("tenant_access.duration", "ms");

    public async Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken)
    {
        using var activity = Traces.StartActivity("tenant_access.resolve");
        activity?.SetTag("contract.version", 1);
        var started = Stopwatch.GetTimestamp();
        var outcome = "cancelled";
        TenantAccessDecision Finish(TenantAccessOutcome result, AuthorizedTenantMembership? membership = null)
        {
            outcome = result.ToString();
            return new(result, membership);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = credential.BearerToken;
            if (string.IsNullOrWhiteSpace(token)) return Finish(TenantAccessOutcome.Unauthenticated);
            // Two safe GETs at most; delay and body consumption share the overall deadline.
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                Attempts.Add(1, new KeyValuePair<string, object?>("attempt", attempt));
                activity?.SetTag("attempt.count", attempt);
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"api/v1/tenant-access/{selectedOrganisationId:D}");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                try
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                    if (response.StatusCode == HttpStatusCode.Unauthorized) return Finish(TenantAccessOutcome.Unauthenticated);
                    if (response.StatusCode == HttpStatusCode.Forbidden) return Finish(TenantAccessOutcome.Denied);
                    if (!response.IsSuccessStatusCode)
                    {
                        Failures.Add(1, new KeyValuePair<string, object?>("reason", "http"));
                        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500;
                        if (!transient || attempt == 2) return Finish(TenantAccessOutcome.Unavailable);
                    }
                    else
                    {
                        var wire = await response.Content.ReadFromJsonAsync<AccessResponse>(cancellationToken: deadline.Token);
                        if (wire is null || wire.ContractVersion != 1 || wire.SubjectId != authenticatedSubjectId
                            || wire.OrganisationId != selectedOrganisationId || wire.OrganisationMembershipId == Guid.Empty
                            || string.IsNullOrWhiteSpace(wire.DecisionVersion) || wire.ObservedAtUtc == default
                            || wire.EffectivePermissionCodes is null
                            || wire.EffectivePermissionCodes.Any(p => !PermissionCatalogue.IsKnown(p))
                            || wire.EffectivePermissionCodes.Distinct(StringComparer.Ordinal).Count() != wire.EffectivePermissionCodes.Length)
                        {
                            Failures.Add(1, new KeyValuePair<string, object?>("reason", "contract"));
                            return Finish(TenantAccessOutcome.Unavailable);
                        }
                        return Finish(TenantAccessOutcome.Authorized, new(wire.OrganisationId, wire.OrganisationMembershipId,
                            wire.EffectivePermissionCodes, wire.DecisionVersion, wire.ObservedAtUtc));
                    }
                }
                catch (HttpRequestException)
                {
                    Failures.Add(1, new KeyValuePair<string, object?>("reason", "transport"));
                    if (attempt == 2) return Finish(TenantAccessOutcome.Unavailable);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
            return Finish(TenantAccessOutcome.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("reason", "timeout"));
            return Finish(TenantAccessOutcome.Unavailable);
        }
        catch (JsonException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("reason", "malformed"));
            return Finish(TenantAccessOutcome.Unavailable);
        }
        catch (NotSupportedException)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("reason", "malformed"));
            return Finish(TenantAccessOutcome.Unavailable);
        }
        finally
        {
            activity?.SetTag("outcome", outcome);
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("contract.version", 1));
        }
    }

    private sealed record AccessResponse(int ContractVersion, string SubjectId, Guid OrganisationId,
        Guid OrganisationMembershipId, string[] EffectivePermissionCodes, string DecisionVersion,
        DateTimeOffset ObservedAtUtc);
}
