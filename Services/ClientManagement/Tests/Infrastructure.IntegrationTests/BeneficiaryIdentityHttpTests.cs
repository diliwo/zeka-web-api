using System.Net;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed partial class DossierLookupHttpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Beneficiary_identity_returns_only_the_selected_lookup_item(bool assignedOnly)
    {
        var access = new Access(assignedOnly);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        var item = Assert.Single((await Lookup(client, "Synthetic")).Items);

        using var response = await client.GetAsync(IdentityRoute(item.Id));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "beneficiaryId", "contractVersion", "displayName" }, Keys(root));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        Assert.Equal(id, root.GetProperty("beneficiaryId").GetInt32());
        Assert.Equal(item.Name, root.GetProperty("displayName").GetString());
        Assert.Equal(2, access.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Beneficiary_identity_hides_foreign_and_assigned_only_inaccessible_ids(bool assignedOnly)
    {
        var access = new Access(assignedOnly);
        var foreign = await Seed(Guid.NewGuid(), Guid.NewGuid());
        var unassigned = await Seed(access.Organisation, Guid.NewGuid());
        var noCase = await Seed(access.Organisation, access.Membership, assigned: false);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);

        var missing = await ReadIdentityNotFound(client, int.MaxValue);
        Assert.Equal(missing, await ReadIdentityNotFound(client, foreign));
        if (assignedOnly)
        {
            Assert.Equal(missing, await ReadIdentityNotFound(client, unassigned));
            Assert.Equal(missing, await ReadIdentityNotFound(client, noCase));
        }
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Unauthenticated, HttpStatusCode.Unauthorized)]
    [InlineData(TenantAccessOutcome.Denied, HttpStatusCode.Forbidden)]
    [InlineData(TenantAccessOutcome.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Beneficiary_identity_rechecks_authority_and_preserves_denial_statuses(
        TenantAccessOutcome outcome, HttpStatusCode status)
    {
        var access = new Access(assignedOnly: false);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        using var before = await client.GetAsync(IdentityRoute(id));
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        access.Outcome = outcome;

        foreach (var target in new[] { id, int.MaxValue })
        {
            using var response = await client.GetAsync(IdentityRoute(target));
            Assert.Equal(status, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(3, access.Calls);
    }

    [Fact]
    public async Task Beneficiary_identity_rejects_anonymous_wrong_organisation_and_edit_only_requests()
    {
        var access = new Access(assignedOnly: false);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var anonymous = app.GetTestClient();
        using var challenge = await anonymous.GetAsync(IdentityRoute(id));
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal(0, access.Calls);

        using var wrong = Client(app, Guid.NewGuid());
        using var denied = await wrong.GetAsync(IdentityRoute(id));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await denied.Content.ReadAsStringAsync());

        access.Permissions = ["Clients.EditAll", "Clients.EditAssigned"];
        using var editOnly = Client(app, access.Organisation);
        using var noViewPermission = await editOnly.GetAsync(IdentityRoute(id));
        Assert.Equal(HttpStatusCode.Forbidden, noViewPermission.StatusCode);
        Assert.Empty(await noViewPermission.Content.ReadAsStringAsync());
    }

    private static string IdentityRoute(int id) => $"/api/v1/beneficiaries/{id}/identity";

    private static async Task<string> ReadIdentityNotFound(HttpClient client, int id)
    {
        using var response = await client.GetAsync(IdentityRoute(id));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.All(root.EnumerateObject(), field =>
            Assert.Contains(field.Name, new[] { "type", "title", "status", "traceId" }));
        return JsonSerializer.Serialize(root.EnumerateObject().Where(field => field.Name != "traceId")
            .OrderBy(field => field.Name).ToDictionary(field => field.Name, field => field.Value.Clone()));
    }
}
