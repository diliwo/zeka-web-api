using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClientManagement.Application;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Lifecycle;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Authorization;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Zeka.Extensions.MultiTenancy.AspNetCore;
using Zeka.PersistenceSecurity;
using ActionHistory = ClientManagement.Core.Entities.ActionChange;

namespace Infrastructure.IntegrationTests;

[Trait("Feature", "B2")]
[Trait("Evidence", "ProviderReal")]
public sealed class LinkedInteractionTests(PostgreSqlClientRuntimeDatabase database, ITestOutputHelper output)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static int sequence = 9200;
    private const string Note = "Synthetic B2 interaction";
    private const string Purpose = "Synthetic B2 follow up";

    [Fact]
    public async Task Linked_creation_commits_exact_response_and_one_consistent_attributed_bundle()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        var access = Grant(subject, org, actor);
        var authority = new AuthorityProbe();
        await using var app = await Host(access, authority);
        using var client = Client(app, org, subject);
        var key = Guid.NewGuid().ToString("D");
        var response = await Post(client, seed.ClientId, Payload(actor, key: key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(new[] { "contractVersion", "followUpActionId", "interactionId", "revision" },
            json.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal(1, json.GetProperty("contractVersion").GetInt32());
        Assert.Equal(1, json.GetProperty("revision").GetInt32());
        var interactionId = json.GetProperty("interactionId").GetInt32();
        var actionId = json.GetProperty("followUpActionId").GetInt32();
        Assert.True(interactionId > 0 && actionId > 0);
        await AssertBundle(org, seed, actor, interactionId, actionId, key, Note, Purpose);
        Assert.Equal(1, authority.TimeZoneCalls);
        Assert.Equal(1, authority.EligibilityCalls);
        Assert.DoesNotContain(Note, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Purpose, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Omitted_action_preserves_B1_shape_and_needs_neither_action_authority()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe { ZoneAvailable = false, Unavailable = true };
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        var response = await Post(client, seed.ClientId, Payload(actor, linked: false), "?unchanged=allowed");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await Json(response);
        Assert.Equal(new[] { "contractVersion", "createdAtUtc", "interactionId", "revision" },
            json.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal(0, authority.TimeZoneCalls);
        Assert.Equal(0, authority.EligibilityCalls);
        await Counts(org, 1, 0, 0, 1);
    }

    [Theory]
    [InlineData("null-action")]
    [InlineData("array-action")]
    [InlineData("unknown-root")]
    [InlineData("duplicate-root")]
    [InlineData("missing-note")]
    [InlineData("unknown-action")]
    [InlineData("duplicate-action")]
    [InlineData("missing-purpose")]
    [InlineData("purpose-number")]
    [InlineData("purpose-null")]
    [InlineData("purpose-empty")]
    [InlineData("purpose-blank")]
    [InlineData("purpose-long")]
    [InlineData("purpose-nul")]
    [InlineData("purpose-control")]
    [InlineData("purpose-surrogate")]
    [InlineData("note-surrogate")]
    [InlineData("date-default")]
    [InlineData("date-invalid")]
    [InlineData("date-noncanonical")]
    [InlineData("date-instant")]
    [InlineData("member-empty")]
    [InlineData("member-uppercase")]
    [InlineData("member-noncanonical")]
    [InlineData("key-uppercase")]
    [InlineData("key-non-v4")]
    [InlineData("time-offset")]
    [InlineData("time-precision")]
    [InlineData("time-future")]
    public async Task Linked_wire_rejects_invalid_shape_or_value_without_writes(string variant)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        var text = InvalidPayload(Payload(actor), variant);
        var response = await Post(client, seed.ClientId, text);
        await Problem(response, HttpStatusCode.BadRequest);
        Assert.Equal(0, authority.TimeZoneCalls);
        Assert.Equal(0, authority.EligibilityCalls);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("query")]
    [InlineData("media")]
    public async Task Linked_post_rejects_query_or_wrong_media_without_writes(string variant)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        using var content = new StringContent(Payload(actor), Encoding.UTF8,
            variant == "media" ? "text/plain" : "application/json");
        var response = await client.PostAsync(Path(seed.ClientId) + (variant == "query" ? "?unexpected=1" : ""), content);
        await Problem(response, HttpStatusCode.BadRequest);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData(8192, HttpStatusCode.Created)]
    [InlineData(8193, HttpStatusCode.RequestEntityTooLarge)]
    public async Task Unknown_length_streaming_body_enforces_total_byte_ceiling(int size, HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe());
        using var client = Client(app, org, subject);
        var body = Payload(actor);
        body += new string(' ', size - Encoding.UTF8.GetByteCount(body));
        using var content = new UnknownLengthContent(body);
        var response = await client.PostAsync(Path(seed.ClientId), content);
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) await Problem(response, expected);
        await Counts(org, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0);
    }

    [Theory]
    [InlineData("2000-01-01")]
    [InlineData("today")]
    public async Task Past_or_today_due_date_and_exact_Unicode_text_are_preserved(string date)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe());
        using var client = Client(app, org, subject);
        var note = string.Concat(Enumerable.Repeat("🔒", 500));
        var purpose = " \t" + new string('p', 496) + "\n ";
        var day = date == "today" ? DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd") : date;
        var response = await Post(client, seed.ClientId, Payload(actor, note: note, purpose: purpose, day: day));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = Context(org);
        Assert.Equal(note, (await db.Interactions.SingleAsync()).Note);
        var action = await db.FollowUpActions.SingleAsync();
        Assert.Equal(purpose, action.Purpose);
        Assert.Equal(DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture), action.DueLocalDate);
    }

    [Theory]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("view-only", HttpStatusCode.Forbidden)]
    [InlineData("revoked", HttpStatusCode.Forbidden)]
    [InlineData("wrong-org", HttpStatusCode.Forbidden)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable)]
    public async Task Current_caller_authority_is_required_before_target_authority(string mode, HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var access = new Access(); var authority = new AuthorityProbe();
        if (mode == "view-only") access.Grant(subject, org, actor, "Clients.ViewAll");
        if (mode == "wrong-org") access.Grant(subject, Guid.NewGuid(), actor, "Clients.EditAll");
        if (mode == "unavailable") access.Unavailable = true;
        await using var app = await Host(access, authority);
        using var client = Client(app, org, mode == "anonymous" ? null : subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), expected);
        Assert.Equal(0, authority.TimeZoneCalls);
        Assert.Equal(0, authority.EligibilityCalls);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cross-tenant")]
    [InlineData("unassigned")]
    public async Task Hidden_resources_share_redacted_404_and_do_not_expose_target_ineligibility(string mode)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(mode == "cross-tenant" ? Guid.NewGuid() : org, Guid.NewGuid());
        var authority = new AuthorityProbe { Eligible = false };
        var access = Grant(subject, org, actor, mode == "unassigned" ? "Clients.EditAssigned" : "Clients.EditAll");
        await using var app = await Host(access, authority);
        using var client = Client(app, org, subject);
        var id = mode == "missing" ? int.MaxValue : seed.ClientId;
        await Problem(await Post(client, id, Payload(actor)), HttpStatusCode.NotFound);
        Assert.Equal(0, authority.EligibilityCalls);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("All", false, true, HttpStatusCode.Created)]
    [InlineData("Assigned", false, true, HttpStatusCode.Created)]
    [InlineData("Assigned", true, true, HttpStatusCode.BadRequest)]
    [InlineData("All", false, false, HttpStatusCode.BadRequest)]
    public async Task Target_scope_is_current_eligibility_not_a_caller_grant(string scope, bool wrongWorker,
        bool eligible, HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        var authority = new AuthorityProbe { TargetScope = scope, Eligible = eligible };
        await using var app = await Host(Grant(subject, org, actor, "Clients.EditAssigned"), authority);
        using var client = Client(app, org, subject);
        var response = await Post(client, seed.ClientId, Payload(wrongWorker ? Guid.NewGuid() : actor));
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) await Problem(response, expected);
        await Counts(org, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0);
    }

    [Theory]
    [InlineData("note")]
    [InlineData("purpose")]
    [InlineData("date")]
    [InlineData("assignee")]
    [InlineData("action-presence")]
    public async Task Exact_replay_returns_original_ids_and_changed_payload_conflicts(string changed)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        var body = Payload(actor);
        var first = await Json(await Post(client, seed.ClientId, body));
        var repeat = await Post(client, seed.ClientId, body);
        Assert.Equal(HttpStatusCode.Created, repeat.StatusCode);
        Assert.Equal(first.GetRawText(), (await Json(repeat)).GetRawText());
        var altered = JsonNode.Parse(body)!.AsObject();
        if (changed == "note") altered["conciseNote"] = "Changed synthetic note";
        if (changed == "purpose") altered["action"]!["purpose"] = "Changed synthetic purpose";
        if (changed == "date") altered["action"]!["dueLocalDate"] = "2001-01-01";
        if (changed == "assignee") altered["action"]!["assigneeMembershipId"] = Guid.NewGuid().ToString("D");
        if (changed == "action-presence") altered.Remove("action");
        await Problem(await Post(client, seed.ClientId, altered.ToJsonString()), HttpStatusCode.Conflict);
        Assert.Equal(1, authority.TimeZoneCalls);
        Assert.Equal(1, authority.EligibilityCalls);
        await Counts(org, 1, 1, 1, 1);
    }

    [Fact]
    public async Task Interaction_only_key_cannot_be_reused_for_linked_creation()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe());
        using var client = Client(app, org, subject);
        var body = Payload(actor, linked: false);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, seed.ClientId, body)).StatusCode);
        var linked = JsonNode.Parse(body)!.AsObject();
        linked["action"] = JsonNode.Parse(JsonNode.Parse(Payload(actor))!["action"]!.ToJsonString());
        await Problem(await Post(client, seed.ClientId, linked.ToJsonString()), HttpStatusCode.Conflict);
        await Counts(org, 1, 0, 0, 1);
    }

    [Fact]
    public async Task Concurrent_exact_requests_commit_one_bundle_and_return_the_same_ids()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe());
        using var first = Client(app, org, subject); using var second = Client(app, org, subject);
        var body = Payload(actor);
        var responses = await Task.WhenAll(Post(first, seed.ClientId, body), Post(second, seed.ClientId, body));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Equal((await Json(responses[0])).GetRawText(), (await Json(responses[1])).GetRawText());
        await Counts(org, 1, 1, 1, 1);
    }

    [Fact]
    public async Task EditAll_replay_keeps_original_case_and_survives_later_authority_loss_but_not_revocation()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe(); var access = Grant(subject, org, actor);
        await using var app = await Host(access, authority);
        using var client = Client(app, org, subject);
        var body = Payload(actor);
        var first = await Json(await Post(client, seed.ClientId, body));
        await CaseChange(org, seed.CaseId, "close");
        await AddCase(org, seed);
        authority.ZoneAvailable = false; authority.Unavailable = true;
        var response = await Post(client, seed.ClientId, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(first.GetRawText(), (await Json(response)).GetRawText());
        await using (var db = Context(org))
            Assert.Equal(seed.CaseId, (await db.Interactions.SingleAsync()).SocialCaseId);
        access.Revoke(subject);
        await Problem(await Post(client, seed.ClientId, body), HttpStatusCode.Forbidden);
        Assert.Equal(1, authority.TimeZoneCalls);
        Assert.Equal(1, authority.EligibilityCalls);
        await Counts(org, 1, 1, 1, 1);
    }

    [Theory]
    [InlineData("timezone", HttpStatusCode.ServiceUnavailable)]
    [InlineData("visibility", HttpStatusCode.NotFound)]
    public async Task Assigned_replay_requires_current_timezone_and_dossier_visibility(string loss,
        HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe { TargetScope = "Assigned" };
        await using var app = await Host(Grant(subject, org, actor, "Clients.EditAssigned"), authority);
        using var client = Client(app, org, subject);
        var body = Payload(actor);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, seed.ClientId, body)).StatusCode);
        if (loss == "timezone") authority.ZoneAvailable = false;
        else await CaseChange(org, seed.CaseId, "close");
        await Problem(await Post(client, seed.ClientId, body), expected);
        await Counts(org, 1, 1, 1, 1);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("ambiguous")]
    [InlineData("invalid-worker")]
    public async Task New_linked_write_requires_exactly_one_valid_current_case(string context)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        if (context == "none") await CaseChange(org, seed.CaseId, "close");
        if (context == "ambiguous") await AddCase(org, seed);
        if (context == "invalid-worker") await Execute(org,
            "UPDATE public.\"SocialWorkers\" SET \"Softdelete\"=true WHERE \"SocialWorkerId\"=@id",
            c => c.Parameters.AddWithValue("id", seed.WorkerId));
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.Conflict);
        Assert.Equal(0, authority.EligibilityCalls);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("history")]
    [InlineData("receipt")]
    public async Task Failure_at_each_persistence_stage_rolls_back_the_complete_bundle(string stage)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe(),
            saveFailure: new SaveFailure(stage));
        using var client = Client(app, org, subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.ServiceUnavailable);
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Created)]
    [InlineData(true, HttpStatusCode.ServiceUnavailable)]
    public async Task Ambiguous_commit_reads_receipt_only_and_missing_receipt_is_not_success(bool rollBack,
        HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        var ambiguity = new AmbiguityProbe { BeforeCommit = rollBack };
        await using var app = await Host(Grant(subject, org, actor), authority, ambiguity: ambiguity);
        using var client = Client(app, org, subject);
        var response = await Post(client, seed.ClientId, Payload(actor));
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) await Problem(response, expected);
        Assert.Equal(1, authority.TimeZoneCalls);
        Assert.Equal(1, authority.EligibilityCalls);
        Assert.Equal(1, ambiguity.Injected);
        await Counts(org, rollBack ? 0 : 1, rollBack ? 0 : 1, rollBack ? 0 : 1, rollBack ? 0 : 1);
    }

    [Theory]
    [InlineData("insert", HttpStatusCode.Conflict)]
    [InlineData("close", HttpStatusCode.Conflict)]
    [InlineData("delete", HttpStatusCode.Conflict)]
    [InlineData("restore", HttpStatusCode.Conflict)]
    [InlineData("worker", HttpStatusCode.BadRequest)]
    public async Task Second_connection_changes_after_eligibility_are_rechecked_under_existing_locks(
        string change, HttpStatusCode expected)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var restoreId = 0;
        if (change == "restore") { restoreId = await AddCase(org, seed); await CaseChange(org, restoreId, "delete"); }
        var authority = new AuthorityProbe { TargetScope = "Assigned" };
        authority.AfterEligibility = async () =>
        {
            if (change == "insert") await AddCase(org, seed);
            else if (change == "worker") await WorkerChange(org, seed.WorkerId, Guid.NewGuid());
            else await CaseChange(org, change == "restore" ? restoreId : seed.CaseId, change);
        };
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), expected);
        await Counts(org, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Observed_target_revocation_permits_one_residual_and_denies_the_next_new_key()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        var authority = new AuthorityProbe();
        authority.AfterEligibility = () => { authority.Eligible = false; return Task.CompletedTask; };
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, seed.ClientId, Payload(actor))).StatusCode);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.BadRequest);
        await Counts(org, 1, 1, 1, 1);
    }

    [Fact]
    public async Task Stale_observation_at_final_PostgreSQL_guard_rolls_back_every_row()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        await using var app = await Host(Grant(subject, org, actor),
            new AuthorityProbe { ObservationAge = TimeSpan.FromSeconds(3) });
        using var client = Client(app, org, subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.ServiceUnavailable);
        await Counts(org, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Linked_case_resolution_uses_organisation_day_not_host_day_and_final_day_guard_stays_closed()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor);
        var fakeUtc = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Today.AddDays(10)
            .AddHours(23).AddMinutes(30), DateTimeKind.Utc));
        var localDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fakeUtc,
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels")).DateTime);
        await CaseStart(org, seed.CaseId, localDay.ToDateTime(TimeOnly.MinValue));
        var attempts = new AttemptProbe();
        await using var app = await Host(Grant(subject, org, actor, "Clients.EditAssigned"),
            new AuthorityProbe { TargetScope = "Assigned" }, clock: new FixedClock(fakeUtc), attempts: attempts);
        using var client = Client(app, org, subject);
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.ServiceUnavailable);
        Assert.Contains(attempts.Events, e => e.Category == TenantAttemptCommandCategory.EfWrite);
        Assert.Contains(attempts.Events, e => e.Category == TenantAttemptCommandCategory.Rollback);
        await Counts(org, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Closure_fence_and_closed_gate_prevent_new_or_saved_linked_writes()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe(); var gate = new LinkedGate();
        await using var app = await Host(Grant(subject, org, actor), authority, gate: gate);
        using var client = Client(app, org, subject);
        var body = Payload(actor);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, seed.ClientId, body)).StatusCode);
        gate.Open = false;
        await Problem(await Post(client, seed.ClientId, body), HttpStatusCode.ServiceUnavailable);
        gate.Open = true;
        await Fence(org);
        await Problem(await Post(client, seed.ClientId, body), HttpStatusCode.ServiceUnavailable);
        await Counts(org, 1, 1, 1, 1);
    }

    [Fact]
    public async Task Production_route_discovery_and_gate_stay_closed_and_B2_host_opens_creation_only()
    {
        Assert.DoesNotContain(typeof(ClientManagement.API.Controllers.ClientsController).Assembly.GetTypes(),
            t => t == typeof(BeneficiaryB1TestController)
                || t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), true)
                    .OfType<Microsoft.AspNetCore.Mvc.RouteAttribute>()
                    .Any(a => a.Template?.Contains("interactions", StringComparison.OrdinalIgnoreCase) == true
                        || a.Template?.Contains("actions", StringComparison.OrdinalIgnoreCase) == true));
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority, productionGate: true);
        using var client = Client(app, org, subject);
        Assert.IsType<ClosedActionWriteActivationGate>(app.Services.GetRequiredService<IActionWriteActivationGate>());
        await Problem(await Post(client, seed.ClientId, Payload(actor)), HttpStatusCode.ServiceUnavailable);
        Assert.Equal(0, authority.TimeZoneCalls);
        var gate = new LinkedGate();
        foreach (var kind in Enum.GetValues<ActionChangeKind>())
            Assert.Throws<TenantAccessException>(() => gate.DemandActionChange(kind));
        await Counts(org, 0, 0, 0, 0);
    }

    [Theory]
    [InlineData("malformed", HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData("semantic-denial", HttpStatusCode.BadRequest, 1)]
    [InlineData("dependency-auth", HttpStatusCode.ServiceUnavailable, 1)]
    [InlineData("transient", HttpStatusCode.Created, 2)]
    [InlineData("timeout", HttpStatusCode.ServiceUnavailable, 1)]
    public async Task Existing_HTTP_authority_adapter_is_composed_with_B2_without_semantic_retries(string mode,
        HttpStatusCode expected, int expectedEligibilityCalls)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var transport = new AuthorityTransport(org, actor, mode);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe(), transport: transport);
        using var client = Client(app, org, subject);
        var response = await Post(client, seed.ClientId, Payload(actor));
        Assert.Equal(expected, response.StatusCode);
        if (expected != HttpStatusCode.Created) await Problem(response, expected);
        Assert.Equal(1, transport.ZoneCalls);
        Assert.Equal(expectedEligibilityCalls, transport.EligibilityCalls);
        await Counts(org, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0, expected == HttpStatusCode.Created ? 1 : 0,
            expected == HttpStatusCode.Created ? 1 : 0);
    }

    [Fact]
    public void Canonical_timezone_policy_matches_all_340_reviewed_members_and_manifest_checksum()
    {
        var type = typeof(IActionAuthorityServiceCredential).Assembly.GetType(
            "ClientManagement.Infrastructure.Authorization.ActionAuthorityClient", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var members = Assert.IsAssignableFrom<IEnumerable<string>>(
            type.GetField("CanonicalIanaIds", flags)!.GetValue(null)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(340, members.Length);
        Assert.Equal(340, members.Distinct(StringComparer.Ordinal).Count());
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", members) + "\n");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        Assert.Equal("6916cb132692f2b2c990b3b8da7ead8819e3ca606acc9b1a093143f80c7c733d", hash);
        var membership = type.GetMethod("IsCanonicalIanaId", flags)!;
        Assert.NotNull(membership);
        Assert.All(members, id => Assert.True((bool)membership.Invoke(null, new object?[] { id })!,
            "Every reviewed canonical member must be admitted by the private membership predicate."));
        var manifest = Assert.IsType<string>(type.GetField("CanonicalIanaManifest", flags)!.GetRawConstantValue());
        Assert.Equal(members, manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Canonical_timezone_membership_rejects_null_overlength_controls_and_normalization()
    {
        var type = typeof(IActionAuthorityServiceCredential).Assembly.GetType(
            "ClientManagement.Infrastructure.Authorization.ActionAuthorityClient", throwOnError: true)!;
        var membership = type.GetMethod("IsCanonicalIanaId", BindingFlags.NonPublic | BindingFlags.Static)!;
        var invalid = new string?[] { null, "", new string('x', 129), "Europe/Brussels\0",
            "europe/brussels", " Europe/Brussels", "Europe/Brussels ", "Europe/Brussels\n",
            "Europe/Amsterdam", "Europe/Kiev", "Asia/Calcutta", "US/Eastern", "UTC", "GMT" };
        Assert.All(invalid, id => Assert.False((bool)membership.Invoke(null, new object?[] { id })!,
            "Unreviewed IDs must fail exact membership without trimming, casing or alias conversion."));
    }
    [Theory]
    [InlineData("Europe/Kyiv", true)]
    [InlineData("Asia/Kolkata", true)]
    [InlineData("Europe/Brussels", true)]
    [InlineData("America/New_York", true)]
    [InlineData("Etc/UTC", true)]
    [InlineData("Etc/GMT", true)]
    [InlineData("Europe/Amsterdam", false)]
    [InlineData("Europe/Kiev", false)]
    [InlineData("Asia/Calcutta", false)]
    [InlineData("US/Eastern", false)]
    [InlineData("UTC", false)]
    [InlineData("GMT", false)]
    [InlineData("Romance Standard Time", false)]
    [InlineData("europe/brussels", false)]
    [InlineData(" Europe/Brussels", false)]
    [InlineData("Europe/Brussels ", false)]
    [InlineData("Europe/Brussels\n", false)]
    [InlineData("Europe/Not_A_Zone", false)]
    [InlineData("", false)]
    public async Task Actual_HTTP_timezone_policy_admits_exact_canonical_members_and_rejects_aliases(
        string zoneId, bool accepted)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var transport = new AuthorityTransport(org, actor, "success", zoneId);
        await using var app = await Host(Grant(subject, org, actor), new AuthorityProbe(), transport: transport);
        using var client = Client(app, org, subject);
        var response = await Post(client, seed.ClientId, Payload(actor));
        if (accepted) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        else await Problem(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, transport.ZoneCalls);
        Assert.Equal(accepted ? 1 : 0, transport.EligibilityCalls);
        await Counts(org, accepted ? 1 : 0, accepted ? 1 : 0, accepted ? 1 : 0, accepted ? 1 : 0);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replay")]
    [InlineData("conflict")]
    [InlineData("malformed")]
    [InlineData("denied")]
    [InlineData("hidden")]
    [InlineData("authority-failure")]
    [InlineData("rollback")]
    public async Task Composed_B2_observability_keeps_private_input_out_of_ordinary_channels(string scenario)
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        var privateNote = "B2-private-note-" + suffix;
        var privatePurpose = "B2-private-purpose-" + suffix;
        var privateBeneficiary = "B2-private-beneficiary-" + suffix;
        var privateHeader = "synthetic-b2-header-" + suffix;
        var seed = await Seed(org, actor, privateBeneficiary);
        var access = Grant(subject, org, actor, scenario == "denied" ? "Clients.ViewAll" : "Clients.EditAll");
        using var capture = new PrivacyCapture();
        var transport = new AuthorityTransport(org, actor, scenario == "authority-failure" ? "dependency-auth" : "success");
        await using var app = await Host(access, new AuthorityProbe(), transport: transport,
            saveFailure: scenario == "rollback" ? new SaveFailure("history") : null, privacy: capture);
        capture.ProveDetectorAndChannelControls(app.Services.GetRequiredService<ILoggerFactory>());
        using var client = Client(app, org, subject);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", privateHeader);
        var body = Payload(actor, note: privateNote, purpose: privatePurpose);
        var forbidden = new[] { privateNote, privatePurpose, privateBeneficiary, privateHeader,
            "synthetic-service-token", body };
        var target = scenario == "hidden" ? int.MaxValue : seed.ClientId;
        if (scenario is "replay" or "conflict")
            Assert.Equal(HttpStatusCode.Created, (await Post(client, target, body)).StatusCode);
        var request = scenario == "conflict" ? body.Replace(privatePurpose, privatePurpose + "-changed")
            : scenario == "malformed" ? body[..^1] + ",\"unknown\":true}" : body;
        var response = await Post(client, target, request);
        var expected = scenario switch
        {
            "create" or "replay" => HttpStatusCode.Created,
            "conflict" => HttpStatusCode.Conflict,
            "malformed" => HttpStatusCode.BadRequest,
            "denied" => HttpStatusCode.Forbidden,
            "hidden" => HttpStatusCode.NotFound,
            _ => HttpStatusCode.ServiceUnavailable
        };
        Assert.Equal(expected, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.False(PrivacyCapture.ContainsAny(new[] { responseText }, forbidden),
            "Response must not expose private synthetic input.");
        var rows = scenario is "create" or "replay" or "conflict" ? 1 : 0;
        capture.AssertOrdinaryChannelsSafe(forbidden);
        var actualActivityObserved = capture.ActualActivityObserved;
        await Counts(org, rows, rows, rows, rows);
        Assert.Equal(scenario is "malformed" or "denied" or "hidden" ? 0 : 1, transport.ZoneCalls);
        Assert.Equal(scenario is "malformed" or "denied" or "hidden" ? 0 : 1, transport.EligibilityCalls);

        await using var db = Context(org);
        var seedClient = await db.Clients.SingleAsync();
        await db.Interactions.LoadAsync(); await db.FollowUpActions.LoadAsync();
        await db.ActionChanges.LoadAsync(); await db.IdempotencyReceipts.LoadAsync();
        // Inspect actual structural audit columns only. Business Note/Purpose and the
        // protected RecordMetadata receipt payload intentionally remain persisted.
        var audit = db.ChangeTracker.Entries().SelectMany(entry => entry.Properties
            .Where(p => p.Metadata.Name is "CreatedBy" or "Created" or "LastModifiedBy" or "LastModified")
            .Select(p => p.Metadata.Name + "=" + Convert.ToString(p.CurrentValue, CultureInfo.InvariantCulture)))
            .ToArray();
        Assert.True(seedClient.Created > DateTime.MinValue && seedClient.CreatedBy == "ZeKa",
            "Actual persistence audit interceptor must supply the harmless audit positive control.");
        Assert.NotEmpty(audit);
        Assert.True(PrivacyCapture.ContainsAny(audit, new[] { "CreatedBy=ZeKa" }),
            "Persisted audit capture must contain the actual interceptor's structural value.");
        Assert.True(PrivacyCapture.ContainsAny(audit.Append("audit-detector-control"), new[] { "audit-detector-control" }),
            "Audit privacy detector must detect a deliberately introduced in-memory control.");
        Assert.False(PrivacyCapture.ContainsAny(audit, forbidden),
            "Structural audit metadata must not retain private synthetic input.");
        output.WriteLine("B2 privacy: ordinary_log_observed=true; structural_audit_observed=true; "
            + "actual_activity_observed=" + actualActivityObserved.ToString().ToLowerInvariant()
            + "; private_input_absent=true; detector_controls_passed=true");
        if (!actualActivityObserved)
            output.WriteLine("LIMITATION: no actual B2 request/provider Activity was emitted; Activity privacy is untested. "
                + "The harmless test Activity control proves collector sensitivity only.");
    }
    [Fact]
    public async Task Cancellation_during_authority_is_not_success_and_creates_no_rows()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe { WaitForCancellation = true };
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        using var cancel = new CancellationTokenSource();
        var pending = Post(client, seed.ClientId, Payload(actor), cancellationToken: cancel.Token);
        await authority.EligibilityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Counts(org, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Client_row_lock_wait_expires_the_final_authority_observation_and_rolls_back()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        await using var holder = new NpgsqlConnection(database.RuntimeConnectionString);
        await holder.OpenAsync(); await using var tx = await holder.BeginTransactionAsync();
        await SetTenant(holder, tx, org); await LockClient(holder, tx, org, seed.ClientId);
        var pending = Post(client, seed.ClientId, Payload(actor));
        var committed = false;
        try
        {
            await authority.EligibilityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForBlock(holder.ProcessID, pending);
            await using var observer = new NpgsqlConnection(database.AdministratorConnectionString);
            await observer.OpenAsync();
            var end = DateTimeOffset.UtcNow.AddSeconds(5);
            while (true)
            {
                await using var age = new NpgsqlCommand(
                    "SELECT clock_timestamp() > @observed + interval '2100 milliseconds'", observer);
                age.Parameters.AddWithValue("observed", authority.LastEligibilityObserved.UtcDateTime);
                if (await age.ExecuteScalarAsync() is true) break;
                Assert.False(pending.IsCompleted);
                if (DateTimeOffset.UtcNow >= end) throw new TimeoutException("PostgreSQL observation-age barrier timed out");
                await Task.Delay(20);
            }
            await tx.CommitAsync(); committed = true;
            await Problem(await pending.WaitAsync(TimeSpan.FromSeconds(5)), HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            if (!committed) await tx.RollbackAsync();
        }
        await Counts(org, 0, 0, 0, 0);
    }

    [Fact]
    public async Task Caller_cancellation_during_locked_write_is_not_replayed_or_committed()
    {
        var org = Guid.NewGuid(); var actor = Guid.NewGuid(); var subject = Guid.NewGuid();
        var seed = await Seed(org, actor); var authority = new AuthorityProbe();
        await using var app = await Host(Grant(subject, org, actor), authority);
        using var client = Client(app, org, subject);
        await using var holder = new NpgsqlConnection(database.RuntimeConnectionString);
        await holder.OpenAsync(); await using var tx = await holder.BeginTransactionAsync();
        await SetTenant(holder, tx, org); await LockClient(holder, tx, org, seed.ClientId);
        using var cancel = new CancellationTokenSource();
        var pending = Post(client, seed.ClientId, Payload(actor), cancellationToken: cancel.Token);
        try
        {
            await authority.EligibilityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForBlock(holder.ProcessID, pending);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { await tx.RollbackAsync(); }
        Assert.Equal(1, authority.EligibilityCalls);
        await Counts(org, 0, 0, 0, 0);
    }

    private static async Task LockClient(NpgsqlConnection connection, NpgsqlTransaction tx, Guid org, int id)
    {
        await using var command = new NpgsqlCommand(
            "SELECT 1 FROM public.\"Clients\" WHERE \"OrganisationId\"=@org AND \"Id\"=@id FOR UPDATE", connection, tx);
        command.Parameters.AddWithValue("org", org); command.Parameters.AddWithValue("id", id);
        Assert.Equal(1, await command.ExecuteScalarAsync());
    }

    private async Task WaitForBlock(int holder, Task pending)
    {
        await using var observer = new NpgsqlConnection(database.AdministratorConnectionString);
        await observer.OpenAsync();
        var end = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < end)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_stat_activity WHERE @holder = ANY(pg_catalog.pg_blocking_pids(pid)))",
                observer);
            command.Parameters.AddWithValue("holder", holder);
            if (await command.ExecuteScalarAsync() is true) { Assert.False(pending.IsCompleted); return; }
            Assert.False(pending.IsCompleted);
            await Task.Delay(20);
        }
        throw new TimeoutException("The B2 write did not reach its observed PostgreSQL row-lock wait");
    }

    private async Task<WebApplication> Host(Access access, AuthorityProbe authority, LinkedGate? gate = null,
        SaveFailure? saveFailure = null, AmbiguityProbe? ambiguity = null, bool productionGate = false,
        TimeProvider? clock = null, AttemptProbe? attempts = null, AuthorityTransport? transport = null,
        PrivacyCapture? privacy = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (privacy is not null)
        {
            // Test-host-only memory observer. Sensitive-data logging remains disabled.
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            builder.Logging.AddProvider(privacy);
            builder.Logging.AddFilter<PrivacyCapture>("Microsoft.AspNetCore", LogLevel.Information);
        }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = System.IO.Path.GetTempPath(),
            ["ActionAuthority:AuthManagementUrl"] = "https://auth.synthetic.invalid/"
        });
        builder.Services.AddTenantContext(); builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IOperationIdentity, RequestIdentity>();
        builder.Services.AddApplication(); builder.Services.AddInfrastructure(builder.Configuration);
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<ICurrentTenantAccess>();
        builder.Services.AddSingleton<ICurrentTenantAccess>(access);
        if (!productionGate)
        {
            builder.Services.RemoveAll<IActionWriteActivationGate>();
            builder.Services.AddSingleton<IActionWriteActivationGate>(gate ?? new LinkedGate());
        }
        if (transport is null)
        {
            builder.Services.RemoveAll<IActionAuthorityReads>();
            builder.Services.AddScoped<IActionAuthorityReads>(sp =>
                new SyntheticAuthority(sp.GetRequiredService<ApplicationDbContext>(), authority));
        }
        else
        {
            builder.Services.RemoveAll<IActionAuthorityServiceCredential>();
            builder.Services.AddSingleton<IActionAuthorityServiceCredential>(new ServiceCredential());
            builder.Services.AddHttpClient("ActionAuthority").ConfigurePrimaryHttpMessageHandler(() => transport);
        }
        if (saveFailure is not null) builder.Services.AddSingleton<IInterceptor>(saveFailure);
        if (clock is not null) { builder.Services.RemoveAll<TimeProvider>(); builder.Services.AddSingleton(clock); }
        if (attempts is not null)
        {
            builder.Services.RemoveAll<ITenantAttemptOrderObserver>();
            builder.Services.AddSingleton<ITenantAttemptOrderObserver>(attempts);
        }
        if (ambiguity is not null)
        {
            var original = builder.Services.Last(x => x.ServiceType == typeof(ITenantTransactionExecutor));
            builder.Services.Remove(original);
            builder.Services.AddScoped<ITenantTransactionExecutor>(sp => new AmbiguousExecutor(
                (ITenantTransactionExecutor)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), ambiguity));
        }
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Clear();
            parts.ApplicationParts.Add(new AssemblyPart(typeof(BeneficiaryB1TestController).Assembly));
        });
        var app = builder.Build();
        app.UseMiddleware<BeneficiaryB1ProblemMiddleware>(); app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static Access Grant(Guid subject, Guid org, Guid actor, string permission = "Clients.EditAll")
    { var access = new Access(); access.Grant(subject, org, actor, permission); return access; }

    private static HttpClient Client(WebApplication app, Guid org, Guid? subject)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Organisation-Id", org.ToString("D"));
        if (subject is not null) client.DefaultRequestHeaders.Add("X-Test-Subject", subject.Value.ToString("D"));
        return client;
    }

    private static string Path(int id) => $"/api/v1/beneficiaries/{id}/interactions";
    private static Task<HttpResponseMessage> Post(HttpClient client, int id, string body, string query = "",
        CancellationToken cancellationToken = default) => client.PostAsync(Path(id) + query,
        new StringContent(body, Encoding.UTF8, "application/json"), cancellationToken);
    private static DateTime UtcMilliseconds() => new(DateTime.UtcNow.Ticks
        / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static string Payload(Guid assignee, string? key = null, bool linked = true,
        string note = Note, string purpose = Purpose, string? day = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["occurredAtUtc"] = UtcMilliseconds().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
            ["conciseNote"] = note, ["idempotencyKey"] = key ?? Guid.NewGuid().ToString("D")
        };
        if (linked) body["action"] = new { purpose,
            dueLocalDate = day ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd"),
            assigneeMembershipId = assignee.ToString("D") };
        return JsonSerializer.Serialize(body);
    }

    private static string InvalidPayload(string text, string variant)
    {
        if (variant == "duplicate-root") return text[..^1] + ",\"idempotencyKey\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\"}";
        if (variant == "duplicate-action") return text.Replace("\"purpose\":", "\"purpose\":\"duplicate\",\"purpose\":");
        if (variant == "purpose-surrogate") return text.Replace(JsonSerializer.Serialize(Purpose), "\"\\ud800\"");
        if (variant == "note-surrogate") return text.Replace(JsonSerializer.Serialize(Note), "\"\\ud800\"");
        var node = JsonNode.Parse(text)!.AsObject();
        switch (variant)
        {
            case "null-action": node["action"] = null; break;
            case "array-action": node["action"] = new JsonArray(); break;
            case "unknown-root": node["caseId"] = 42; break;
            case "missing-note": node.Remove("conciseNote"); break;
            case "unknown-action": node["action"]!["state"] = "Open"; break;
            case "missing-purpose": node["action"]!.AsObject().Remove("purpose"); break;
            case "purpose-number": node["action"]!["purpose"] = 42; break;
            case "purpose-null": node["action"]!["purpose"] = null; break;
            case "purpose-empty": node["action"]!["purpose"] = ""; break;
            case "purpose-blank": node["action"]!["purpose"] = " \t\n"; break;
            case "purpose-long": node["action"]!["purpose"] = new string('p', 501); break;
            case "purpose-nul": node["action"]!["purpose"] = "before\0after"; break;
            case "purpose-control": node["action"]!["purpose"] = "before\rafter"; break;
            case "date-default": node["action"]!["dueLocalDate"] = "0001-01-01"; break;
            case "date-invalid": node["action"]!["dueLocalDate"] = "2026-02-30"; break;
            case "date-noncanonical": node["action"]!["dueLocalDate"] = "2026-1-2"; break;
            case "date-instant": node["action"]!["dueLocalDate"] = "2026-10-25T00:00:00Z"; break;
            case "member-empty": node["action"]!["assigneeMembershipId"] = Guid.Empty.ToString("D"); break;
            case "member-uppercase": node["action"]!["assigneeMembershipId"] = "BBBBBBBB-BBBB-4BBB-8BBB-BBBBBBBBBBBB"; break;
            case "member-noncanonical": node["action"]!["assigneeMembershipId"] = Guid.NewGuid().ToString("N"); break;
            case "key-uppercase": node["idempotencyKey"] = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"; break;
            case "key-non-v4": node["idempotencyKey"] = "aaaaaaaa-aaaa-1aaa-8aaa-aaaaaaaaaaaa"; break;
            case "time-offset": node["occurredAtUtc"] = "2026-10-25T12:00:00+00:00"; break;
            case "time-precision": node["occurredAtUtc"] = "2026-10-25T12:00:00.0000Z"; break;
            case "time-future": node["occurredAtUtc"] = UtcMilliseconds().AddMinutes(6).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); break;
            default: throw new InvalidOperationException("Unknown synthetic wire variant");
        }
        return node.ToJsonString();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.Clone(); }

    private static async Task Problem(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        Assert.Equal(new[] { "status", "title", "type" },
            json.RootElement.EnumerateObject().Select(x => x.Name).OrderBy(x => x).ToArray());
        Assert.Equal((int)expected, json.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain(Note, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Purpose, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgres", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic-service-token", text, StringComparison.Ordinal);
    }

    private async Task<(int ClientId, int CaseId, int WorkerId)> Seed(Guid org, Guid actor, string? privateReference = null)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var db = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await db.Database.UseTransactionAsync(tx);
        var client = ClientManagement.Tests.Common.SyntheticClient.Create(
            ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: Interlocked.Increment(ref sequence)));
        client.ReferenceNumber = privateReference ?? $"b2-{Guid.NewGuid():N}";
        var worker = new SocialWorker();
        worker.ApplyProjection(actor, 1, Guid.NewGuid(), true, "Synthetic", "Worker",
            Guid.NewGuid().ToString("N"), "Team", "T");
        var socialCase = new SocialCase(client, DateTime.Today.AddDays(-5), worker, "Synthetic seed");
        db.Add(socialCase); await db.SaveChangesAsync(); await tx.CommitAsync();
        return (client.Id, socialCase.Id, worker.Id);
    }

    private ApplicationDbContext Context(Guid org) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(database.AdministratorConnectionString).Options,
        TenantEnforcementTests.Scope(org));

    private async Task Counts(Guid org, int interactions, int actions, int history, int receipts)
    {
        await using var db = Context(org);
        Assert.Equal(interactions, await db.Interactions.CountAsync());
        Assert.Equal(actions, await db.FollowUpActions.CountAsync());
        Assert.Equal(history, await db.ActionChanges.CountAsync());
        Assert.Equal(receipts, await db.IdempotencyReceipts.CountAsync());
    }

    private async Task AssertBundle(Guid org, (int ClientId, int CaseId, int WorkerId) seed, Guid actor,
        int interactionId, int actionId, string key, string note, string purpose)
    {
        await using var db = Context(org);
        var interaction = await db.Interactions.SingleAsync();
        var action = await db.FollowUpActions.SingleAsync();
        var history = await db.ActionChanges.SingleAsync();
        var receipt = await db.IdempotencyReceipts.SingleAsync();
        Assert.Equal(interactionId, interaction.Id); Assert.Equal(seed.ClientId, interaction.ClientId);
        Assert.Equal(seed.CaseId, interaction.SocialCaseId); Assert.Equal(actor, interaction.AuthorMembershipId);
        Assert.Equal(note, interaction.Note);
        Assert.Equal(actionId, action.Id); Assert.Equal(org, action.OrganisationId);
        Assert.Equal(seed.ClientId, action.ClientId); Assert.Equal(seed.CaseId, action.SocialCaseId);
        Assert.Equal(interaction.Id, action.InteractionId); Assert.Equal(actor, action.CreatorMembershipId);
        Assert.Equal(actor, action.AssigneeMembershipId); Assert.Equal(purpose, action.Purpose);
        Assert.Equal("Open", action.State); Assert.Equal(1, action.Version);
        Assert.Equal("Create", history.Operation); Assert.Equal(actor, history.ActorMembershipId);
        Assert.Equal(action.Id, history.FollowUpActionId); Assert.Equal(0, history.PriorVersion); Assert.Equal(1, history.NewVersion);
        Assert.Equal(actor, receipt.ActorMembershipId); Assert.Equal("RecordInteraction.v1", receipt.Operation);
        Assert.Equal(key, receipt.ClientKey); Assert.Equal(interaction.Id, receipt.InteractionId);
        Assert.Equal(action.Id, receipt.FollowUpActionId); Assert.Equal(1, receipt.ResultVersion);
    }

    private async Task CaseChange(Guid org, int id, string change)
    {
        var sql = change switch
        {
            "close" => "UPDATE public.\"SocialCases\" SET \"EndDate\"=@day WHERE \"SchoolRegistrationId\"=@id",
            "delete" => "UPDATE public.\"SocialCases\" SET \"Softdelete\"=true WHERE \"SchoolRegistrationId\"=@id",
            "restore" => "UPDATE public.\"SocialCases\" SET \"Softdelete\"=false WHERE \"SchoolRegistrationId\"=@id",
            _ => throw new InvalidOperationException("Unknown synthetic case change")
        };
        await Execute(org, sql, c =>
        {
            c.Parameters.AddWithValue("id", id);
            if (change == "close") c.Parameters.AddWithValue("day", DateTime.Today.AddDays(-1));
        });
    }

    private Task CaseStart(Guid org, int id, DateTime day) => Execute(org,
        "UPDATE public.\"SocialCases\" SET \"StartDate\"=@day WHERE \"SchoolRegistrationId\"=@id",
        c => { c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("day", day); });

    private Task WorkerChange(Guid org, int id, Guid member) => Execute(org,
        "UPDATE public.\"SocialWorkers\" SET \"OrganisationMembershipId\"=@member WHERE \"SocialWorkerId\"=@id",
        c => { c.Parameters.AddWithValue("id", id); c.Parameters.AddWithValue("member", member); });

    private async Task<int> AddCase(Guid org, (int ClientId, int CaseId, int WorkerId) seed)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var db = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await db.Database.UseTransactionAsync(tx);
        var client = await db.Clients.SingleAsync(x => x.Id == seed.ClientId);
        var worker = await db.SocialWorkers.SingleAsync(x => x.Id == seed.WorkerId);
        var socialCase = new SocialCase(client, DateTime.Today.AddDays(-2), worker, "Synthetic replacement");
        db.Add(socialCase); await db.SaveChangesAsync(); await tx.CommitAsync(); return socialCase.Id;
    }

    private async Task Fence(Guid org)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var db = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await db.Database.UseTransactionAsync(tx);
        db.OrganisationClosureFences.Add(OrganisationClosureFence.Enter(Guid.NewGuid(), org, 1,
            "b2-synthetic", "synthetic-hash", "synthetic-fence", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }

    private async Task Execute(Guid org, string sql, Action<NpgsqlCommand> parameters)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync(); await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org); await using var command = new NpgsqlCommand(sql, connection, tx);
        parameters(command); Assert.Equal(1, await command.ExecuteNonQueryAsync()); await tx.CommitAsync();
    }

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction tx, Guid org)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('zeka.organisation_id', @org, true)", connection, tx);
        command.Parameters.AddWithValue("org", org.ToString("D")); await command.ExecuteNonQueryAsync();
    }

    private sealed class RequestIdentity(IHttpContextAccessor context) : IOperationIdentity
    {
        public string SubjectId => context.HttpContext?.Request.Headers["X-Test-Subject"].ToString() ?? "";
        public Guid SelectedOrganisationId => Guid.TryParse(context.HttpContext?.Request.Headers["X-Organisation-Id"]
            .ToString(), out var org) ? org : Guid.Empty;
    }

    private sealed class Access : ICurrentTenantAccess
    {
        private readonly Dictionary<Guid, (Guid Org, Guid Actor, string[] Permissions)> grants = new();
        public bool Unavailable;
        public void Grant(Guid subject, Guid org, Guid actor, params string[] permissions) =>
            grants[subject] = (org, actor, permissions);
        public void Revoke(Guid subject) => grants.Remove(subject);
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid org, CancellationToken token)
        {
            if (Unavailable) return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Unavailable));
            if (!Guid.TryParse(subject, out var id) || !grants.TryGetValue(id, out var grant) || grant.Org != org)
                return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Denied));
            return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
                new AuthorizedTenantMembership(org, grant.Actor, grant.Permissions, "b2-synthetic", DateTimeOffset.UtcNow)));
        }
    }

    private sealed class LinkedGate : IActionWriteActivationGate
    {
        public bool Open = true;
        public void DemandActionWrite() { if (!Open) throw new TenantAccessException(AccessFailure.Unavailable); }
        public void DemandActionChange(ActionChangeKind kind) => throw new TenantAccessException(AccessFailure.Unavailable);
    }

    private sealed class AuthorityProbe
    {
        public bool ZoneAvailable = true, Eligible = true, Unavailable, WaitForCancellation;
        public string TargetScope = "All";
        public TimeSpan ObservationAge;
        public int TimeZoneCalls, EligibilityCalls;
        public DateTimeOffset LastEligibilityObserved;
        public Func<Task>? AfterEligibility;
        public TaskCompletionSource EligibilityEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SyntheticAuthority(ApplicationDbContext database, AuthorityProbe probe) : IActionAuthorityReads
    {
        public Task<ActionTimeZone> TimeZoneAsync(Guid org, Guid subject, Guid actor, CancellationToken token)
        {
            Assert.Null(database.Database.CurrentTransaction); Interlocked.Increment(ref probe.TimeZoneCalls);
            if (!probe.ZoneAvailable) throw new TenantAccessException(AccessFailure.Unavailable);
            return Task.FromResult(new ActionTimeZone("Europe/Brussels", 1, DateTimeOffset.UtcNow - probe.ObservationAge,
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Brussels")));
        }
        public async Task<AssigneeDecision?> EligibilityAsync(Guid org, Guid subject, Guid actor, Guid target,
            CancellationToken token)
        {
            Assert.Null(database.Database.CurrentTransaction); Interlocked.Increment(ref probe.EligibilityCalls);
            var observed = DateTimeOffset.UtcNow - probe.ObservationAge; var eligible = probe.Eligible;
            probe.LastEligibilityObserved = observed;
            probe.EligibilityEntered.TrySetResult();
            if (probe.WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (probe.Unavailable) throw new TenantAccessException(AccessFailure.Unavailable);
            if (probe.AfterEligibility is not null) await probe.AfterEligibility();
            return eligible ? new AssigneeDecision(probe.TargetScope, "b2-synthetic", observed) : null;
        }
    }

    private sealed class SaveFailure(string stage) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var fail = eventData.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added &&
                (stage == "action" && e.Entity is FollowUpAction || stage == "history" && e.Entity is ActionHistory
                    || stage == "receipt" && e.Entity is IdempotencyReceipt));
            if (fail) throw new TenantAccessException(AccessFailure.Unavailable);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AmbiguityProbe { public bool BeforeCommit; public int Injected; }
    private sealed class AmbiguousExecutor(ITenantTransactionExecutor inner, AmbiguityProbe probe) : ITenantTransactionExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> f, CancellationToken t) => inner.ExecuteAsync(f, t);
        public Task ExecuteAsync(Func<CancellationToken, Task> f, CancellationToken t) => inner.ExecuteAsync(f, t);
        public Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> f, CancellationToken t) => inner.ExecuteOrdinaryAsync(f, t);
        public Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> f, CancellationToken t) => inner.ExecuteOrdinaryAsync(f, t);
        public Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> f, CancellationToken t) => inner.ExecuteLifecycleAsync(f, t);
        public Task ExecuteLifecycleAsync(Func<CancellationToken, Task> f, CancellationToken t) => inner.ExecuteLifecycleAsync(f, t);
        public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> f, CancellationToken t) => inner.ExecuteOnceAsync(f, t);
        public async Task<T> ExecuteOrdinaryOnceAsync<T>(Func<CancellationToken, Task<T>> f, CancellationToken t)
        {
            var result = await inner.ExecuteOrdinaryOnceAsync(async token =>
            {
                var value = await f(token);
                if (probe.BeforeCommit && value is RecordInteractionResult && Interlocked.CompareExchange(ref probe.Injected, 1, 0) == 0)
                    throw new TenantCommitIndeterminateException(new Exception("Synthetic lost outcome before commit"));
                return value;
            }, t);
            if (!probe.BeforeCommit && result is RecordInteractionResult && Interlocked.CompareExchange(ref probe.Injected, 1, 0) == 0)
                throw new TenantCommitIndeterminateException(new Exception("Synthetic lost commit acknowledgement"));
            return result;
        }
    }

    private sealed class FixedClock(DateTimeOffset utc) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => utc; }

    private sealed class AttemptProbe : ITenantAttemptOrderObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<TenantAttemptOrderEvent> events = new();
        public TenantAttemptOrderEvent[] Events => events.ToArray();
        public void Observe(TenantAttemptOrderEvent evidence) => events.Enqueue(evidence);
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly string text;
        public UnknownLengthContent(string text)
        {
            this.text = text;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();
    }

    private sealed class PrivacyCapture : ILoggerProvider, ISupportExternalScope
    {
        private const string ControlSource = "Zeka.B2.Privacy.Control";
        private readonly ConcurrentQueue<LogObservation> logs = new();
        private readonly ConcurrentQueue<ActivityObservation> activities = new();
        private readonly ActivityListener listener;
        private IExternalScopeProvider scopes = new LoggerExternalScopeProvider();
        private int disposed;

        public PrivacyCapture()
        {
            listener = new ActivityListener
            {
                ShouldListenTo = _ => true,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => activities.Enqueue(new ActivityObservation(activity.Source.Name,
                    activity.OperationName, activity.DisplayName, activity.Status.ToString(), activity.StatusDescription,
                    string.Join(";", activity.TagObjects.Select(p => p.Key + "=" + Render(p.Value))),
                    string.Join(";", activity.Events.Select(e => e.Name + ":" + string.Join(",",
                        e.Tags.Select(p => p.Key + "=" + Render(p.Value))))),
                    string.Join(";", activity.Baggage.Select(p => p.Key + "=" + p.Value))))
            };
            ActivitySource.AddActivityListener(listener);
        }

        public bool ActualActivityObserved => activities.Any(a => a.Source != ControlSource);
        public ILogger CreateLogger(string categoryName) => new MemoryLogger(this, categoryName);
        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => scopes = scopeProvider;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) listener.Dispose();
        }

        public void ProveDetectorAndChannelControls(ILoggerFactory factory)
        {
            // Deliberately harmless controls, never the request's private markers.
            const string control = "b2-detector-positive-control";
            var logger = factory.CreateLogger("B2.Privacy.Control");
            using (logger.BeginScope(new Dictionary<string, object?> { ["control.scope"] = control }))
                logger.LogInformation(new InvalidOperationException(control),
                    "B2 " + control + " {ControlValue}", control);
            var observed = logs.ToArray();
            Assert.True(observed.Any(e => e.Formatted.Contains(control, StringComparison.Ordinal)
                && e.Structured.Contains(control, StringComparison.Ordinal)
                && e.Template.Contains(control, StringComparison.Ordinal)
                && e.Scopes.Contains(control, StringComparison.Ordinal)
                && e.Exception.Contains(control, StringComparison.Ordinal)),
                "The real logging provider must observe formatted, structured, template, scope and exception controls.");
            Assert.True(ContainsAny(observed.SelectMany(e => e.AllValues()), new[] { control }),
                "The ordinary-log privacy detector must detect the deliberate control.");
            using (var source = new ActivitySource(ControlSource))
            {
                using var activity = source.StartActivity("b2.activity." + control);
                Assert.NotNull(activity);
                activity!.DisplayName = "b2.display." + control;
                activity.SetTag("control.tag", control);
                activity.AddBaggage("control.baggage", control);
                activity.AddEvent(new ActivityEvent(control, tags: new ActivityTagsCollection { { "control.event", control } }));
                activity.SetStatus(ActivityStatusCode.Error, control);
            }
            Assert.True(activities.Any(a => a.Source == ControlSource
                && a.Operation.Contains(control, StringComparison.Ordinal)
                && a.Display.Contains(control, StringComparison.Ordinal)
                && a.Tags.Contains(control, StringComparison.Ordinal)
                && a.Events.Contains(control, StringComparison.Ordinal)
                && a.Baggage.Contains(control, StringComparison.Ordinal)
                && a.Status == "Error" && a.Description?.Contains(control, StringComparison.Ordinal) == true),
                "Activity listener must observe actual names, tags, event fields, baggage and status-description controls.");
            Assert.True(ContainsAny(activities.SelectMany(a => a.AllValues()), new[] { control }),
                "The Activity privacy detector must detect the deliberate control.");
            logs.Clear(); activities.Clear();
            logger.LogInformation("B2 ordinary-log observation control is active");
        }

        public void AssertOrdinaryChannelsSafe(string[] forbidden)
        {
            var observed = logs.ToArray();
            Assert.NotEmpty(observed);
            Assert.True(observed.Any(e => e.Category.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)),
                "Capture must observe real composed ASP.NET request logs, not only a custom positive control.");
            Assert.False(ContainsAny(observed.SelectMany(e => e.AllValues()), forbidden),
                "Composed ordinary formatted/state/template/scope/exception logs must not retain private input.");
            Assert.False(ContainsAny(activities.SelectMany(a => a.AllValues()), forbidden),
                "Observed actual Activity names/tags/events/baggage/status fields must not retain private input.");
        }

        public static bool ContainsAny(IEnumerable<string?> values, IEnumerable<string> forbidden) =>
            values.Any(value => value is not null && forbidden.Any(marker =>
                value.Contains(marker, StringComparison.Ordinal)));

        private static string Render(object? value)
        {
            if (value is null) return "";
            if (value is IEnumerable<KeyValuePair<string, object?>> fields)
                return string.Join(";", fields.Select(p => p.Key + "=" + Render(p.Value)));
            if (value is string text) return text;
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "";
            try { return JsonSerializer.Serialize(value, value.GetType()); }
            catch (NotSupportedException) { return value.ToString() ?? ""; }
            catch (JsonException) { return value.ToString() ?? ""; }
        }

        private sealed class MemoryLogger(PrivacyCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner.scopes.Push(state);
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var scopeValues = new List<string>();
                owner.scopes.ForEachScope((value, buffer) => buffer.Add(Render(value)), scopeValues);
                var template = state is IEnumerable<KeyValuePair<string, object?>> fields
                    ? fields.FirstOrDefault(p => p.Key == "{OriginalFormat}").Value?.ToString() ?? "" : "";
                owner.logs.Enqueue(new LogObservation(category, formatter(state, exception), Render(state), template,
                    string.Join(";", scopeValues), exception?.ToString() ?? ""));
            }
        }

        private sealed record LogObservation(string Category, string Formatted, string Structured,
            string Template, string Scopes, string Exception)
        {
            public IEnumerable<string?> AllValues() => new[] { Category, Formatted, Structured, Template, Scopes, Exception };
        }
        private sealed record ActivityObservation(string Source, string Operation, string Display,
            string Status, string? Description, string Tags, string Events, string Baggage)
        {
            public IEnumerable<string?> AllValues() => new[] { Source, Operation, Display, Status, Description, Tags, Events, Baggage };
        }
    }
    private sealed class ServiceCredential : IActionAuthorityServiceCredential
    { public string? BearerToken => "synthetic-service-token"; }

    private sealed class AuthorityTransport(Guid org, Guid actor, string mode,
        string timeZoneId = "Europe/Brussels") : HttpMessageHandler
    {
        public int ZoneCalls, EligibilityCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("synthetic-service-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath == "/internal/v1/organisation-time-zone")
            {
                Interlocked.Increment(ref ZoneCalls);
                return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { contractVersion = 1,
                    organisationId = org, timeZoneId, configVersion = 1,
                    observedAtUtc = DateTimeOffset.UtcNow }));
            }
            Assert.Equal("/internal/v1/assignee-eligibility", request.RequestUri.AbsolutePath);
            var calls = Interlocked.Increment(ref EligibilityCalls);
            if (mode == "timeout") await Task.Delay(Timeout.InfiniteTimeSpan, token);
            if (mode == "dependency-auth") return Response(HttpStatusCode.Unauthorized, "{}");
            if (mode == "transient" && calls == 1) return Response(HttpStatusCode.ServiceUnavailable, "{}");
            if (mode == "malformed") return Response(HttpStatusCode.OK, "{\"contractVersion\":1,\"eligible\":true}");
            if (mode == "semantic-denial") return Response(HttpStatusCode.OK, "{\"contractVersion\":1,\"eligible\":false}");
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return Response(HttpStatusCode.OK, JsonSerializer.Serialize(new { contractVersion = 1,
                organisationId = org, actorMembershipId = actor,
                targetMembershipId = document.RootElement.GetProperty("targetMembershipId").GetGuid(),
                eligible = true, targetViewScope = "All", decisionVersion = "b2-synthetic",
                observedAtUtc = DateTimeOffset.UtcNow }));
        }
        private static HttpResponseMessage Response(HttpStatusCode code, string body) => new(code)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
