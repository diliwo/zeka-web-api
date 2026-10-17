using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClientManagement.Application.Common.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed partial class DossierLookupHttpTests
{
    private const string LookupRoute = "/api/v1/beneficiaries/lookup";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_orders_caps_and_preserves_duplicate_ids_after_authorization(bool assignedOnly)
    {
        var access = new Access(assignedOnly);
        var expected = new List<(int Id, string Name)>();
        // Insert in reverse order and include duplicate names across the page boundary.
        for (var i = 24; i >= 0; i--)
        {
            var last = $"Lookup{i / 2:D2}";
            expected.Add((await Seed(access.Organisation, access.Membership, "Person", last), last + " Person"));
        }
        // These earlier-sorting rows must not consume the authorized bound.
        for (var i = 0; i < 22; i++)
        {
            await Seed(Guid.NewGuid(), Guid.NewGuid(), "LookupForeign", "AAA");
            if (assignedOnly) await Seed(access.Organisation, Guid.NewGuid(), "LookupHidden", "AAA");
        }
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        var ordered = expected.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).Take(20).ToArray();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await Lookup(client, "  lOoKuP  ");
            Assert.True(result.HasMore);
            Assert.Equal(ordered.Select(x => x.Id), result.Items.Select(x => x.Id));
            Assert.Equal(ordered.Select(x => x.Name), result.Items.Select(x => x.Name));
            Assert.Equal(20, result.Items.Select(x => x.Id).Distinct().Count());
        }
        Assert.Equal(2, access.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(21)]
    public async Task Lookup_hasMore_depends_only_on_an_additional_authorized_row(int count)
    {
        var access = new Access(assignedOnly: true);
        for (var i = 0; i < count; i++) await Seed(access.Organisation, access.Membership);
        // Hidden same-tenant, no-case and foreign rows cannot change items or hasMore.
        for (var i = 0; i < 22; i++)
        {
            await Seed(access.Organisation, Guid.NewGuid());
            await Seed(Guid.NewGuid(), Guid.NewGuid());
        }
        await Seed(access.Organisation, access.Membership, assigned: false);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        var result = await Lookup(client, "Synthetic");
        Assert.Equal(Math.Min(20, count), result.Items.Length);
        Assert.Equal(count > 20, result.HasMore);
    }

    [Fact]
    public async Task Lookup_is_name_only_and_keeps_legacy_list_search_and_detail_fields()
    {
        var access = new Access(assignedOnly: false);
        var id = await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        foreach (var name in new[] { "synthetic", "CLIENT" })
            Assert.Equal(id, Assert.Single((await Lookup(client, name)).Items).Id);
        foreach (var nonName in new[] { "dossier-test", "example.invalid", "Test street" })
            Assert.Empty((await Lookup(client, nonName)).Items);
        using var search = await client.PostAsJsonAsync("/api/clients/search", new { searchText = "dossier-test" });
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync());
        var legacy = Assert.Single(searchJson.RootElement.GetProperty("clients").EnumerateArray());
        var niss = legacy.GetProperty("ssn").GetString()!;
        Assert.Empty((await Lookup(client, niss)).Items);
        using var list = await client.GetAsync("/api/clients");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        foreach (var item in new[] { legacy, Assert.Single(listJson.RootElement.GetProperty("clients").EnumerateArray()) })
        {
            Assert.Equal(new[] { "clientId", "emailAddress", "gender", "name", "ssn" }, Keys(item));
            Assert.Equal(id, item.GetProperty("clientId").GetInt32());
            Assert.Equal("Client Synthetic", item.GetProperty("name").GetString());
            Assert.Equal("synthetic@example.invalid", item.GetProperty("emailAddress").GetString());
            Assert.Equal(niss, item.GetProperty("ssn").GetString());
        }
        // The separate existing direct-detail tests assert the complete legacy success shape and 404 equivalence.
    }

    [Fact]
    public async Task Lookup_refreshes_permissions_and_membership_without_a_positive_cache()
    {
        var access = new Access(assignedOnly: false);
        var own = await Seed(access.Organisation, access.Membership);
        var otherMembership = Guid.NewGuid();
        var other = await Seed(access.Organisation, otherMembership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        Assert.Equal(2, (await Lookup(client, "Synthetic")).Items.Length);
        access.Permissions = ["Clients.ViewAssigned"];
        Assert.Equal(own, Assert.Single((await Lookup(client, "Synthetic")).Items).Id);
        access.Membership = otherMembership;
        Assert.Equal(other, Assert.Single((await Lookup(client, "Synthetic")).Items).Id);
        access.Permissions = [];
        using var denied = await client.PostAsJsonAsync(LookupRoute, new { searchText = "Synthetic" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(await denied.Content.ReadAsStringAsync());
        Assert.Equal(4, access.Calls);
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Unauthenticated, HttpStatusCode.Unauthorized)]
    [InlineData(TenantAccessOutcome.Denied, HttpStatusCode.Forbidden)]
    [InlineData(TenantAccessOutcome.Unavailable, HttpStatusCode.ServiceUnavailable)]
    public async Task Lookup_authority_failures_preserve_non_disclosing_statuses(
        TenantAccessOutcome outcome, HttpStatusCode status)
    {
        var access = new Access(assignedOnly: false);
        await Seed(access.Organisation, access.Membership);
        await using var app = await Host(access);
        using var client = Client(app, access.Organisation);
        Assert.Single((await Lookup(client, "Synthetic")).Items);
        access.Outcome = outcome;
        foreach (var text in new[] { "Synthetic", "NoMatchingName" })
        {
            using var response = await client.PostAsJsonAsync(LookupRoute, new { searchText = text });
            Assert.Equal(status, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(3, access.Calls);
    }

    [Fact]
    public async Task Lookup_rejects_anonymous_wrong_organisation_and_edit_only_authority()
    {
        var access = new Access(assignedOnly: false);
        await using var app = await Host(access);
        using var anonymous = app.GetTestClient();
        using var challenge = await anonymous.PostAsJsonAsync(LookupRoute, new { searchText = "Synthetic" });
        Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
        Assert.Equal(0, access.Calls);
        using var wrong = Client(app, Guid.NewGuid());
        using var wrongResponse = await wrong.PostAsJsonAsync(LookupRoute, new { searchText = "Synthetic" });
        Assert.Equal(HttpStatusCode.Forbidden, wrongResponse.StatusCode);
        access.Permissions = ["Clients.EditAll", "Clients.EditAssigned"];
        using var client = Client(app, access.Organisation);
        using var denied = await client.PostAsJsonAsync(LookupRoute, new { searchText = "Synthetic" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Lookup_invalid_bodies_and_logs_do_not_echo_submitted_values()
    {
        var access = new Access(assignedOnly: false);
        var logs = new LookupLogs();
        await using var app = await Host(access, logs);
        using var client = Client(app, access.Organisation);
        const string sensitive = "SyntheticPrivateSearchValue";
        foreach (var body in new[] { "", "null", "{}", "{\"searchText\":null}", "{\"searchText\":\"ab\"}",
            "{\"searchText\":\" a b \"}", "{\"searchText\":\"   \"}",
            "{\"searchText\":[\"" + sensitive + "\"]}", "{\"searchText\":{\"" + sensitive + "\":true}}",
            "{\"searchText\":\"" + sensitive })
        {
            using var response = await client.PostAsync(LookupRoute, new StringContent(body, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain(sensitive, await response.Content.ReadAsStringAsync());
            Assert.Equal(LookupRoute, response.RequestMessage!.RequestUri!.AbsolutePath);
            Assert.Empty(response.RequestMessage.RequestUri.Query);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Empty((await Lookup(client, sensitive)).Items);
        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, message => message.Contains(sensitive, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lookup_runtime_rls_rejects_missing_scope_and_excludes_foreign_rows_without_ef()
    {
        var organisation = Guid.NewGuid();
        var own = await Seed(organisation, Guid.NewGuid());
        await Seed(Guid.NewGuid(), Guid.NewGuid());
        await using var connection = new NpgsqlConnection(database.RuntimeConnectionString);
        await connection.OpenAsync();
        await using (var missing = new NpgsqlCommand("SELECT count(*) FROM public.\"Clients\"", connection))
            Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(
                () => missing.ExecuteScalarAsync())).SqlState);
        await using var tx = await connection.BeginTransactionAsync();
        await using (var scope = new NpgsqlCommand("SELECT set_config('zeka.organisation_id', @id, true)", connection, tx))
        {
            scope.Parameters.AddWithValue("id", organisation.ToString("D"));
            await scope.ExecuteNonQueryAsync();
        }
        await using (var rows = new NpgsqlCommand("SELECT \"Id\" FROM public.\"Clients\"", connection, tx))
        await using (var reader = await rows.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(own, reader.GetInt32(0));
            Assert.False(await reader.ReadAsync());
        }
        await tx.CommitAsync();
        await using var after = new NpgsqlCommand("SELECT count(*) FROM public.\"Clients\"", connection);
        Assert.Equal("P0001", (await Assert.ThrowsAsync<PostgresException>(
            () => after.ExecuteScalarAsync())).SqlState);
    }

    private static async Task<(bool HasMore, (int Id, string Name)[] Items)> Lookup(HttpClient client, string text)
    {
        using var response = await client.PostAsJsonAsync(LookupRoute, new { searchText = text });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(LookupRoute, response.RequestMessage!.RequestUri!.AbsolutePath);
        Assert.Empty(response.RequestMessage.RequestUri.Query);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        Assert.Equal(new[] { "contractVersion", "hasMore", "items" }, Keys(root));
        Assert.Equal(1, root.GetProperty("contractVersion").GetInt32());
        var items = root.GetProperty("items").EnumerateArray().Select(item =>
        {
            Assert.Equal(new[] { "beneficiaryId", "displayName" }, Keys(item));
            var id = item.GetProperty("beneficiaryId").GetInt32();
            Assert.True(id > 0);
            return (id, item.GetProperty("displayName").GetString()!);
        }).ToArray();
        Assert.InRange(items.Length, 0, 20);
        return (root.GetProperty("hasMore").GetBoolean(), items);
    }

    private static string[] Keys(JsonElement element) => element.EnumerateObject()
        .Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private sealed class LookupLogs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
        public void Dispose() { }
    }
}
