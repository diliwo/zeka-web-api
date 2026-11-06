using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdminAreaManagement.Application.Common.Authorization;

namespace AdminAreaManagement.Infrastructure.Authorization;

public interface ITenantAccessCredential { string? BearerToken { get; } }

public sealed class CurrentTenantAccessClient(HttpClient client, ITenantAccessCredential credential) : ICurrentTenantAccess
{
    public const string DiagnosticsName = "AdminAreaManagement.TenantAccess";
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    { NumberHandling = JsonNumberHandling.Strict };
    private static readonly ActivitySource Traces = new(DiagnosticsName);
    private static readonly Meter Metrics = new(DiagnosticsName);
    private static readonly Counter<long> Attempts = Metrics.CreateCounter<long>("tenant_access.attempts");
    private static readonly Counter<long> Failures = Metrics.CreateCounter<long>("tenant_access.authority_failures");
    private static readonly Histogram<double> Duration = Metrics.CreateHistogram<double>("tenant_access.duration", "ms");

    public async Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken)
    {
        Activity? activity = null;
        TenantAccessDiagnostics.Observe(() =>
        {
            activity = Traces.StartActivity("tenant_access.resolve");
            activity?.SetTag("contract.version", 1);
        });
        var started = Stopwatch.GetTimestamp();
        var outcome = "cancelled";
        TenantAccessDecision Finish(TenantAccessOutcome result, AuthorizedTenantMembership? membership = null, string? reason = null)
        {
            outcome = result.ToString();
            return new(result, membership, reason);
        }
        TenantAccessDecision Unavailable(string reason)
        {
            TenantAccessDiagnostics.Observe(() => Failures.Add(1, new KeyValuePair<string, object?>("reason", reason)));
            return Finish(TenantAccessOutcome.Unavailable, reason: reason);
        }
        TenantAccessDecision Integrity(string mismatchClass)
        {
            TenantAccessDiagnostics.Integrity(mismatchClass);
            return Unavailable(TenantAccessDiagnostics.IntegrityMismatch);
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
                TenantAccessDiagnostics.Observe(() =>
                {
                    Attempts.Add(1, new KeyValuePair<string, object?>("attempt", attempt));
                    activity?.SetTag("attempt.count", attempt);
                });
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
                        TenantAccessDiagnostics.Observe(() => Failures.Add(1,
                            new KeyValuePair<string, object?>("reason", TenantAccessDiagnostics.DependencyUnavailable)));
                        var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                            || (int)response.StatusCode >= 500;
                        if (!transient || attempt == 2) return Finish(TenantAccessOutcome.Unavailable,
                            reason: TenantAccessDiagnostics.DependencyUnavailable);
                    }
                    else
                    {
                        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                            return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
                        var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in document.RootElement.EnumerateObject())
                            if (!fields.Add(property.Name)) return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
                        if (new[] { "ContractVersion", "SubjectId", "OrganisationId", "OrganisationMembershipId",
                            "EffectivePermissionCodes", "DecisionVersion", "ObservedAtUtc" }.Any(field => !fields.Contains(field)))
                            return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
                        var wire = document.RootElement.Deserialize<AccessResponse>(WireOptions);
                        if (wire is null || wire.ContractVersion != 1 || string.IsNullOrWhiteSpace(wire.SubjectId)
                            || string.IsNullOrWhiteSpace(wire.DecisionVersion) || wire.ObservedAtUtc == default
                            || wire.ObservedAtUtc.Offset != TimeSpan.Zero
                            || !TenantAccessDiagnostics.ValidPermissions(wire.EffectivePermissionCodes))
                            return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
                        if (!StringComparer.Ordinal.Equals(wire.SubjectId, authenticatedSubjectId))
                            return Integrity("subject_mismatch");
                        if (wire.OrganisationId != selectedOrganisationId)
                            return Integrity("organisation_mismatch");
                        if (wire.OrganisationMembershipId == Guid.Empty)
                            return Integrity("contradictory_response");
                        return Finish(TenantAccessOutcome.Authorized, new(wire.OrganisationId, wire.OrganisationMembershipId,
                            TenantAccessDiagnostics.RecognizedPermissions(wire.EffectivePermissionCodes), wire.DecisionVersion, wire.ObservedAtUtc));
                    }
                }
                catch (HttpRequestException)
                {
                    TenantAccessDiagnostics.Observe(() => Failures.Add(1,
                        new KeyValuePair<string, object?>("reason", TenantAccessDiagnostics.DependencyUnavailable)));
                    if (attempt == 2) return Finish(TenantAccessOutcome.Unavailable,
                        reason: TenantAccessDiagnostics.DependencyUnavailable);
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
            }
            return Unavailable(TenantAccessDiagnostics.DependencyUnavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable(TenantAccessDiagnostics.DependencyUnavailable);
        }
        catch (JsonException)
        {
            return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
        }
        catch (NotSupportedException)
        {
            return Unavailable(TenantAccessDiagnostics.ResponseMalformed);
        }
        finally
        {
            TenantAccessDiagnostics.Observe(() =>
            {
                activity?.SetTag("outcome", outcome);
                Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", outcome),
                    new KeyValuePair<string, object?>("contract.version", 1));
            });
            TenantAccessDiagnostics.Observe(() => activity?.Dispose());
        }
    }

    private sealed record AccessResponse(int ContractVersion, string SubjectId, Guid OrganisationId,
        Guid OrganisationMembershipId, string[] EffectivePermissionCodes, string DecisionVersion,
        DateTimeOffset ObservedAtUtc);
}
