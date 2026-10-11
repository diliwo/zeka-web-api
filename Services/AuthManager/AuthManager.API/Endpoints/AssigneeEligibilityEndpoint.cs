using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using AuthManager.Application.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace AuthManager.API.Endpoints;

public static class AssigneeEligibilityEndpoint
{
    private static readonly ActivitySource Traces = new("AuthManager.AssigneeEligibility.v1");
    private static readonly Meter Metrics = new("AuthManager.AssigneeEligibility.v1");
    private static readonly Counter<long> Outcomes = Metrics.CreateCounter<long>("assignee_eligibility.outcomes");
    private static readonly Histogram<double> Latency = Metrics.CreateHistogram<double>("assignee_eligibility.duration_ms");

    public static void MapAssigneeEligibility(this WebApplication app)
    {
        app.MapPost("/internal/v1/assignee-eligibility", async (
            AssigneeEligibilityRequest? request, IAssigneeEligibility resolver,
            AssigneeServiceOptions service, TimeProvider clock, HttpContext http, ILoggerFactory loggers) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var watch = Stopwatch.StartNew();
            using var trace = Traces.StartActivity("assignee_eligibility.decide");
            var outcome = "invalid_request";
            var decisionVersion = "none";
            try
            {
                if (request is null || request.ContractVersion != 1 || request.OrganisationId == Guid.Empty
                    || request.ActorSubjectId == Guid.Empty || request.ActorMembershipId == Guid.Empty
                    || request.TargetMembershipId == Guid.Empty)
                    return Results.BadRequest();

                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var decision = await resolver.ResolveAsync(request, deadline.Token);
                if (decision is null)
                {
                    outcome = "ineligible";
                    return Results.Ok(new { contractVersion = 1, eligible = false });
                }
                var age = clock.GetUtcNow() - decision.ObservedAtUtc;
                if (decision.TargetViewScope is not ("All" or "Assigned")
                    || string.IsNullOrWhiteSpace(decision.DecisionVersion)
                    || decision.DecisionVersion.Length > 256 || decision.ObservedAtUtc.Offset != TimeSpan.Zero
                    || age < TimeSpan.FromSeconds(-30) || age > TimeSpan.FromSeconds(2))
                {
                    outcome = "unavailable";
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }
                decisionVersion = decision.DecisionVersion;
                outcome = "eligible";
                return Results.Ok(new
                {
                    contractVersion = 1, request.OrganisationId, request.ActorMembershipId,
                    request.TargetMembershipId, eligible = true, decision.TargetViewScope,
                    decision.DecisionVersion, decision.ObservedAtUtc
                });
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            { outcome = "unavailable"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (DbException)
            { outcome = "unavailable"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (InvalidOperationException)
            { outcome = "unavailable"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            catch (TimeoutException)
            { outcome = "unavailable"; return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            finally
            {
                watch.Stop();
                trace?.SetTag("outcome", outcome);
                trace?.SetTag("contract.version", 1);
                trace?.SetTag("decision.version", decisionVersion);
                Outcomes.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
                Latency.Record(watch.Elapsed.TotalMilliseconds,
                    new KeyValuePair<string, object?>("outcome", outcome));
                loggers.CreateLogger("AuthManager.AssigneeEligibility")
                    .LogInformation("Assignee eligibility decision: {ServicePrincipal} {ContractVersion} {DecisionVersion} {Outcome} {LatencyMs}.",
                        service.ClientManagementSubject, 1, decisionVersion, outcome, watch.Elapsed.TotalMilliseconds);
            }
        }).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = AssigneeServiceAuthentication.Scheme });
    }
}
