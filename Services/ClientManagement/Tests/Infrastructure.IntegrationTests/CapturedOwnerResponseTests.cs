using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;
using Xunit;
using Xunit.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "ZEK-17")]
public sealed class CapturedOwnerResponseTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Captured_owner_response_accepts_known_grants_without_authorizing_unknown_extras()
    {
        // Sanitized incident facts from the genuine 2026-11-05 discovery response.
        // DecisionVersion and ObservedAtUtc were not exported; these two fields are normalized fixture values.
        const string subject = "6e877fe7-6b03-46ea-a20b-8ac447529970";
        var organisation = Guid.Parse("da913305-5e84-4689-a4b0-a852ab4cb686");
        var membership = Guid.Parse("2318b23e-e3ef-4fed-9f18-ba58fc398d56");
        var reasons = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == CurrentTenantAccessClient.DiagnosticsName
                && instrument.Name == "tenant_access.authority_failures") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags) if (tag.Key == "reason") reasons.Add(tag.Value?.ToString() ?? "");
        });
        listener.Start();
        using var http = new HttpClient(new CapturedResponseHandler(subject, organisation, membership))
            { BaseAddress = new Uri("https://auth.invalid/") };
        var adapter = new CurrentTenantAccessClient(http, new Credential(Guid.NewGuid().ToString("N")));
        var decision = await adapter.ResolveAsync(subject, organisation, default);
        output.WriteLine("Adapter outcome={0}; observed local diagnostic reasons={1}", decision.Outcome, string.Join(",", reasons));
        Assert.Equal(TenantAccessOutcome.Authorized, decision.Outcome);
        Assert.NotNull(decision.Membership);
        Assert.Contains("ReferenceData.View", decision.Membership!.EffectivePermissionCodes);
        Assert.DoesNotContain("Organisations.Export", decision.Membership.EffectivePermissionCodes);
        Assert.DoesNotContain("Organisations.Close", decision.Membership.EffectivePermissionCodes);
        var tenant = new TenantContextScope();
        var operation = new TenantOperation(adapter, new Identity(subject, organisation), tenant, tenant);
        await operation.AuthorizeAsync(new("ReferenceData.View"), default);
        foreach (var permission in new[] { "Organisations.Export", "Organisations.Close" })
        {
            var failure = await Assert.ThrowsAsync<TenantAccessException>(() => operation.AuthorizeAsync(new(permission), default));
            Assert.Equal(AccessFailure.Denied, failure.Failure);
        }
    }

    private sealed record Credential(string? BearerToken) : ITenantAccessCredential;
    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class CapturedResponseHandler(string subject, Guid organisation, Guid membership) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    ContractVersion = 1, SubjectId = subject, OrganisationId = organisation,
                    OrganisationMembershipId = membership,
                    EffectivePermissionCodes = new[]
                    {
                        "Clients.Create", "Clients.Delete", "Clients.EditAll", "Clients.EditAssigned", "Clients.ImportExport",
                        "Clients.ViewAll", "Clients.ViewAssigned", "Organisations.Close", "Organisations.Export",
                        "PartnerDocuments.Delete", "PartnerDocuments.Import", "PartnerDocuments.View", "Partners.Delete",
                        "Partners.Manage", "Partners.View", "ReferenceData.View", "TeamConfiguration.ManageMemberships",
                        "TeamConfiguration.ManageStaffProfiles", "TeamConfiguration.ManageTeams", "TeamConfiguration.View"
                    },
                    DecisionVersion = "normalized-synthetic-fixture", ObservedAtUtc = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero)
                })
            });
    }
}