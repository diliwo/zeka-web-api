using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClientManagement.Application;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Lifecycle;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;
using Zeka.Extensions.MultiTenancy.AspNetCore;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "83")]
[Trait("Evidence", "ProviderReal")]
public sealed class Issue83BeneficiaryB1Tests(PostgreSqlClientRuntimeDatabase database)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static int sequence = 8300;

    [Fact]
    public async Task Interaction_only_http_write_and_reads_are_tenant_scoped_and_revision_one_is_truthful()
    {
        var org = Guid.NewGuid(); var other = Guid.NewGuid(); var member = Guid.NewGuid();
        var own = await Seed(org, member); var hidden = await Seed(other, Guid.NewGuid());
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");

        var context = await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/context");
        Assert.Equal(HttpStatusCode.OK, context.StatusCode);
        var contextJson = await Json(context);
        Assert.Equal("CurrentAuthorisedDossierReaders", contextJson.GetProperty("audience").GetProperty("code").GetString());
        Assert.True(contextJson.GetProperty("canRecordInteraction").GetBoolean());
        Assert.DoesNotContain("ssn", contextJson.GetRawText(), StringComparison.OrdinalIgnoreCase);

        var instant = UtcMilliseconds(); var key = Guid.NewGuid().ToString("D");
        var note = "Synthetic confidential note é 🔒";
        var created = await Post(client, own.ClientId, instant, note, key);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var record = await Json(created);
        var id = record.GetProperty("interactionId").GetInt32();
        Assert.Equal(1, record.GetProperty("revision").GetInt32());
        var recent = await Json(await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions"));
        Assert.False(recent.TryGetProperty("totalCount", out _));
        Assert.Equal(id, recent.GetProperty("items")[0].GetProperty("interactionId").GetInt32());
        var direct = await Json(await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions/{id}"));
        Assert.Equal(note, direct.GetProperty("conciseNote").GetString());
        Assert.Equal(member.ToString("D"), direct.GetProperty("authorMembershipId").GetString());
        Assert.False(direct.TryGetProperty("authorSubjectId", out _));
        var revisions = await Json(await client.GetAsync(
            $"/api/v1/beneficiaries/{own.ClientId}/interactions/{id}/revisions"));
        Assert.Equal(1, revisions.GetProperty("revisions").GetArrayLength());
        Assert.Equal(note, revisions.GetProperty("revisions")[0].GetProperty("conciseNote").GetString());

        var nonexistent = await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions/2147483647");
        var crossTenant = await client.GetAsync($"/api/v1/beneficiaries/{hidden.ClientId}/interactions/{id}");
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);
        Assert.Equal(nonexistent.StatusCode, crossTenant.StatusCode);
        Assert.Equal(await nonexistent.Content.ReadAsStringAsync(), await crossTenant.Content.ReadAsStringAsync());
        Assert.Equal("application/problem+json", nonexistent.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(note, await nonexistent.Content.ReadAsStringAsync());
        Assert.Equal(1, await Count<Interaction>(org));
        Assert.Equal(1, await Count<IdempotencyReceipt>(org));
        Assert.Equal(0, await Count<FollowUpAction>(org));
    }

    [Fact]
    public async Task Receipt_first_exact_retry_keeps_original_case_after_closure_and_context_change()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var instant = UtcMilliseconds(); var key = Guid.NewGuid().ToString("D");
        var first = await Json(await Post(client, own.ClientId, instant, "Original synthetic note", key));
        await EndCase(org, own.CaseId);
        var replacement = await AddCase(org, own.ClientId, own.WorkerId);
        var saved = await Post(client, own.ClientId, instant, "Original synthetic note", key);
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);
        Assert.Equal(first.GetProperty("interactionId").GetInt32(),
            (await Json(saved)).GetProperty("interactionId").GetInt32());
        Assert.Equal(own.CaseId, await InteractionCase(org, first.GetProperty("interactionId").GetInt32()));
        var changed = await Post(client, own.ClientId, instant, "Changed synthetic note", key);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, await Count<Interaction>(org));
        Assert.Equal(1, await Count<IdempotencyReceipt>(org));
        Assert.NotEqual(own.CaseId, replacement);
    }

    [Fact]
    public async Task All_scope_reads_without_case_but_assigned_and_revoked_access_fail_closed()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        await EndCase(org, own.CaseId);
        var access = new Access();
        access.Grant("viewer", org, Guid.NewGuid(), "Clients.ViewAll");
        access.Grant("assigned", org, member, "Clients.ViewAssigned");
        await using var app = await Host(access);
        using var viewer = Client(app, org, "viewer");
        using var assigned = Client(app, org, "assigned");
        var context = await Json(await viewer.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/context"));
        Assert.Equal("None", context.GetProperty("currentCaseState").GetString());
        Assert.False(context.GetProperty("canRecordInteraction").GetBoolean());
        Assert.Equal(HttpStatusCode.OK,
            (await viewer.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await assigned.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/context")).StatusCode);
        access.Revoke("viewer");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/context")).StatusCode);
    }

    [Fact]
    public async Task Broad_view_and_assigned_edit_report_only_current_assigned_record_capability()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid();
        var assigned = await Seed(org, member);
        var unassigned = await Seed(org, Guid.NewGuid());
        var access = new Access();
        access.Grant("dual", org, member, "Clients.ViewAll", "Clients.EditAssigned");
        access.Grant("view-only", org, member, "Clients.ViewAll");
        await using var app = await Host(access);
        using var dual = Client(app, org, "dual");
        using var viewer = Client(app, org, "view-only");

        var ownContext = await Json(await dual.GetAsync($"/api/v1/beneficiaries/{assigned.ClientId}/context"));
        var otherContext = await Json(await dual.GetAsync($"/api/v1/beneficiaries/{unassigned.ClientId}/context"));
        var viewOnlyContext = await Json(await viewer.GetAsync($"/api/v1/beneficiaries/{assigned.ClientId}/context"));
        Assert.True(ownContext.GetProperty("canRecordInteraction").GetBoolean());
        Assert.False(otherContext.GetProperty("canRecordInteraction").GetBoolean());
        Assert.False(viewOnlyContext.GetProperty("canRecordInteraction").GetBoolean());

        Assert.Equal(HttpStatusCode.Created, (await Post(dual, assigned.ClientId, UtcMilliseconds(),
            "Synthetic assigned edit", Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(dual, unassigned.ClientId, UtcMilliseconds(),
            "Synthetic unassigned denial", Guid.NewGuid().ToString("D"))).StatusCode);
        await EndCase(org, assigned.CaseId);
        var revokedContext = await Json(await dual.GetAsync($"/api/v1/beneficiaries/{assigned.ClientId}/context"));
        Assert.False(revokedContext.GetProperty("canRecordInteraction").GetBoolean());
        Assert.Equal(1, await Count<Interaction>(org));
    }

    [Fact]
    public async Task New_write_requires_unique_current_case_and_leaves_no_partial_rows()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var instant = UtcMilliseconds();
        await EndCase(org, own.CaseId);
        Assert.Equal(HttpStatusCode.Conflict,
            (await Post(client, own.ClientId, instant, "No case", Guid.NewGuid().ToString("D"))).StatusCode);
        var first = await AddCase(org, own.ClientId, own.WorkerId);
        var second = await AddCase(org, own.ClientId, own.WorkerId);
        Assert.Equal(HttpStatusCode.Conflict,
            (await Post(client, own.ClientId, instant, "Ambiguous", Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(0, await Count<Interaction>(org));
        Assert.Equal(0, await Count<IdempotencyReceipt>(org));
        await SoftDeleteCase(org, first);
        Assert.Equal(HttpStatusCode.Created,
            (await Post(client, own.ClientId, instant, "One surviving case", Guid.NewGuid().ToString("D"))).StatusCode);
        await SoftDeleteCase(org, second);
        Assert.Equal(HttpStatusCode.Conflict,
            (await Post(client, own.ClientId, instant, "Deleted context", Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(1, await Count<Interaction>(org));
        Assert.Equal(1, await Count<IdempotencyReceipt>(org));
    }

    [Fact]
    public async Task Wire_limits_produce_redacted_problem_without_a_write()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var instant = UtcMilliseconds();
        foreach (var badId in new[] { "0", "01", "-1", "2147483648" })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.GetAsync($"/api/v1/beneficiaries/{badId}/context")).StatusCode);
        foreach (var (time, note, key, expected) in new[]
        {
            (instant.ToString("O"), "Valid", Guid.NewGuid().ToString("D"), HttpStatusCode.BadRequest),
            (instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), new string('é', 1001), Guid.NewGuid().ToString("D"), HttpStatusCode.BadRequest),
            (instant.AddMinutes(6).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), "Valid", Guid.NewGuid().ToString("D"), HttpStatusCode.BadRequest),
            (instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), "Valid", "not-a-uuid", HttpStatusCode.BadRequest),
            (instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), "Valid", "00000000-0000-1000-8000-000000000000", HttpStatusCode.BadRequest)
        })
        {
            var response = await PostText(client, own.ClientId, time, note, key);
            Assert.Equal(expected, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.DoesNotContain(note, await response.Content.ReadAsStringAsync());
        }
        var tooLarge = await PostText(client, own.ClientId, instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            new string('x', 8200), Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(0, await Count<Interaction>(org));
    }

    [Fact]
    public async Task Escaped_NUL_note_is_rejected_with_redacted_400_before_PostgreSQL_write()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var note = "before\0after";
        Assert.Contains("\\u0000", JsonSerializer.Serialize(note), StringComparison.Ordinal);
        var response = await Post(client, own.ClientId, UtcMilliseconds(), note, Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using (var problem = JsonDocument.Parse(body))
            Assert.Equal(400, problem.RootElement.GetProperty("status").GetInt32());
        Assert.DoesNotContain("before", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Postgres", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await Count<Interaction>(org));
        Assert.Equal(0, await Count<IdempotencyReceipt>(org));
    }

    [Fact]
    public async Task Structural_cursor_is_bounded_resource_specific_and_rechecks_current_view()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var second = await Seed(org, Guid.NewGuid());
        await SeedInteractions(org, own.ClientId, own.CaseId, member, 52);
        var access = new Access(); access.Grant("viewer", org, member, "Clients.ViewAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "viewer");
        var first = await Json(await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions"));
        Assert.Equal(20, first.GetProperty("items").GetArrayLength());
        Assert.True(first.GetProperty("hasMore").GetBoolean());
        var cursor = first.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length <= 256);
        Assert.False(first.TryGetProperty("totalCount", out _));
        var next = await Json(await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions?limit=50&cursor={cursor}"));
        Assert.Equal(32, next.GetProperty("items").GetArrayLength());
        Assert.False(next.GetProperty("hasMore").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/beneficiaries/{second.ClientId}/interactions?cursor={cursor}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions?cursor={cursor}x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions?limit=51")).StatusCode);
        access.Revoke("viewer");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/interactions?cursor={cursor}")).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_denied_and_unavailable_authority_are_redacted_without_dossier_data()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access();
        access.Grant("no-permission", org, member);
        access.Grant("other-org", Guid.NewGuid(), Guid.NewGuid(), "Clients.ViewAll");
        access.Unavailable("outage");
        await using var app = await Host(access);
        foreach (var (subject, status) in new[]
        {
            ("", HttpStatusCode.Unauthorized),
            ("no-permission", HttpStatusCode.Forbidden),
            ("other-org", HttpStatusCode.Forbidden),
            ("outage", HttpStatusCode.ServiceUnavailable)
        })
        {
            using var client = Client(app, org, subject);
            var response = await client.GetAsync($"/api/v1/beneficiaries/{own.ClientId}/context");
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(own.ClientId.ToString(), body);
            Assert.DoesNotContain("synthetic", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Closure_fence_denies_new_write_and_rollback_keeps_interaction_and_receipt_empty()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        await using (var connection = new NpgsqlConnection(database.AdministratorConnectionString))
        {
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await SetTenant(connection, tx, org);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
            await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
            await context.Database.UseTransactionAsync(tx);
            context.OrganisationClosureFences.Add(OrganisationClosureFence.Enter(Guid.NewGuid(), org, 1,
                "issue83-test", "synthetic-hash", "synthetic-fence-token", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
            await tx.CommitAsync();
        }
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var response = await Post(client, own.ClientId, UtcMilliseconds(),
            "Synthetic closure note", Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, await Count<Interaction>(org));
        Assert.Equal(0, await Count<IdempotencyReceipt>(org));
    }

    [Fact]
    public async Task Concurrent_closure_fence_wins_before_B1_write_and_prevents_partial_rows()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var observer = new NpgsqlConnection(database.AdministratorConnectionString);
        await observer.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        await using (var barrier = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(pg_catalog.lower(@org), 0))",
            connection, tx))
        {
            barrier.Parameters.AddWithValue("org", org.ToString("D"));
            await barrier.ExecuteNonQueryAsync();
        }
        var pending = Post(client, own.ClientId, UtcMilliseconds(),
            "Synthetic concurrent closure", Guid.NewGuid().ToString("D"));
        await WaitForAdvisoryWaiter(observer, connection.ProcessID, pending);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using (var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org)))
        {
            await context.Database.UseTransactionAsync(tx);
            context.OrganisationClosureFences.Add(OrganisationClosureFence.Enter(Guid.NewGuid(), org, 1,
                "issue83-race", "synthetic-race-hash", "synthetic-race-token", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }
        await tx.CommitAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadAsStringAsync();
        using (var document = JsonDocument.Parse(problem))
        {
            Assert.Equal(503, document.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Service unavailable", document.RootElement.GetProperty("title").GetString());
            Assert.Equal("about:blank", document.RootElement.GetProperty("type").GetString());
        }
        Assert.DoesNotContain("P0001", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("organisation_closure_fence_active", problem, StringComparison.Ordinal);
        Assert.Equal(0, await Count<Interaction>(org));
        Assert.Equal(0, await Count<IdempotencyReceipt>(org));
        Assert.Equal(0, await Count<FollowUpAction>(org));
    }

    [Fact]
    public void Only_the_exact_closure_fence_postgres_error_is_classified_as_authority_unavailable()
    {
        static PostgresException Error(string sqlState, string message) =>
            new(message, "ERROR", "ERROR", sqlState);
        Assert.True(BeneficiaryB1ProblemMiddleware.IsClosureFence(
            new DbUpdateException("write failed", Error("P0001", "organisation_closure_fence_active"))));
        Assert.False(BeneficiaryB1ProblemMiddleware.IsClosureFence(
            new DbUpdateException("write failed", Error("P0001", "unrelated_database_error"))));
        Assert.False(BeneficiaryB1ProblemMiddleware.IsClosureFence(
            new DbUpdateException("write failed", Error("23514", "organisation_closure_fence_active"))));
    }

    [Fact]
    public async Task Unicode_at_1000_UTF16_units_is_accepted_and_1001_units_is_rejected()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        await using var app = await Host(access);
        using var client = Client(app, org, "editor");
        var instant = UtcMilliseconds();
        Assert.Equal(HttpStatusCode.Created, (await Post(client, own.ClientId, instant,
            string.Concat(Enumerable.Repeat("🔒", 500)), Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, own.ClientId, instant,
            string.Concat(Enumerable.Repeat("🔒", 500)) + "x", Guid.NewGuid().ToString("D"))).StatusCode);
        Assert.Equal(1, await Count<Interaction>(org));
    }

    [Fact]
    public async Task Receipt_is_not_returned_to_a_different_editor_after_case_closure()
    {
        var org = Guid.NewGuid(); var originalMember = Guid.NewGuid(); var own = await Seed(org, originalMember);
        var access = new Access();
        access.Grant("original", org, originalMember, "Clients.EditAll");
        access.Grant("other", org, Guid.NewGuid(), "Clients.EditAll");
        await using var app = await Host(access);
        using var original = Client(app, org, "original");
        using var other = Client(app, org, "other");
        var instant = UtcMilliseconds(); var key = Guid.NewGuid().ToString("D");
        var first = await Json(await Post(original, own.ClientId, instant, "Synthetic original", key));
        await EndCase(org, own.CaseId);
        var denied = await Post(other, own.ClientId, instant, "Synthetic original", key);
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(first.GetProperty("interactionId").GetInt32(),
            (await Json(await Post(original, own.ClientId, instant, "Synthetic original", key)))
                .GetProperty("interactionId").GetInt32());
        Assert.Equal(1, await Count<Interaction>(org));
        Assert.Equal(1, await Count<IdempotencyReceipt>(org));
    }

    [Fact]
    public async Task Provider_committed_but_ambiguous_response_reconciles_receipt_only_once()
    {
        var org = Guid.NewGuid(); var member = Guid.NewGuid(); var own = await Seed(org, member);
        var access = new Access(); access.Grant("editor", org, member, "Clients.EditAll");
        var probe = new AmbiguousCommitProbe();
        await using var app = await Host(access, probe);
        using var client = Client(app, org, "editor");
        var response = await Post(client, own.ClientId, UtcMilliseconds(),
            "Synthetic ambiguous commit", Guid.NewGuid().ToString("D"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(2, probe.OrdinaryAttempts); // Committed write then receipt-only reconciliation.
        Assert.Equal(1, await Count<Interaction>(org));
        Assert.Equal(1, await Count<IdempotencyReceipt>(org));
    }

    [Fact]
    public async Task Test_host_route_isolated_from_production_API_assembly_and_gate_stays_closed()
    {
        Assert.DoesNotContain(typeof(ClientManagement.API.Controllers.ClientsController).Assembly.GetTypes(),
            t => t == typeof(BeneficiaryB1TestController)
                || t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), true)
                    .OfType<Microsoft.AspNetCore.Mvc.RouteAttribute>()
                    .Any(a => a.Template?.Contains("beneficiaries", StringComparison.OrdinalIgnoreCase) == true));
        var access = new Access();
        await using var app = await Host(access);
        Assert.IsType<ClosedActionWriteActivationGate>(
            app.Services.GetRequiredService<IActionWriteActivationGate>());
    }

    private async Task<WebApplication> Host(Access access, AmbiguousCommitProbe? ambiguous = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ClientApiConnection"] = database.RuntimeConnectionString,
            ["FileServerPath"] = Path.GetTempPath()
        });
        builder.Services.AddTenantContext();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IOperationIdentity, RequestIdentity>();
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration);
        if (ambiguous is not null)
        {
            var original = builder.Services.Last(x => x.ServiceType == typeof(ITenantTransactionExecutor));
            builder.Services.Remove(original);
            builder.Services.AddSingleton(ambiguous);
            builder.Services.AddScoped<ITenantTransactionExecutor>(sp =>
                new AmbiguousOnceExecutor((ITenantTransactionExecutor)ActivatorUtilities.CreateInstance(
                    sp, original.ImplementationType!), ambiguous));
        }
        builder.Services.RemoveAll<IHostedService>();
        builder.Services.RemoveAll<ICurrentTenantAccess>();
        builder.Services.AddSingleton<ICurrentTenantAccess>(access);
        builder.Services.AddControllers().ConfigureApplicationPartManager(parts =>
        {
            parts.ApplicationParts.Clear();
            parts.ApplicationParts.Add(new AssemblyPart(typeof(BeneficiaryB1TestController).Assembly));
        });
        var app = builder.Build();
        app.UseMiddleware<BeneficiaryB1ProblemMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, Guid org, string subject)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Organisation-Id", org.ToString("D"));
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        return client;
    }

    private static DateTime UtcMilliseconds() => new(DateTime.UtcNow.Ticks
        / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);

    private static Task<HttpResponseMessage> Post(HttpClient client, int beneficiaryId,
        DateTime instant, string note, string key) => PostText(client, beneficiaryId,
        instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"), note, key);

    private static Task<HttpResponseMessage> PostText(HttpClient client, int beneficiaryId,
        string time, string note, string key) => client.PostAsync(
            $"/api/v1/beneficiaries/{beneficiaryId}/interactions",
            new StringContent(JsonSerializer.Serialize(new
            {
                occurredAtUtc = time, conciseNote = note, idempotencyKey = key
            }), Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task<(int ClientId, int CaseId, int WorkerId)> Seed(Guid org, Guid member)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetTenant(connection, transaction, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await context.Database.UseTransactionAsync(transaction);
        var client = ClientManagement.Tests.Common.SyntheticClient.Create(
            ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: Interlocked.Increment(ref sequence)));
        client.ReferenceNumber = $"issue83-{Guid.NewGuid():N}";
        var worker = new SocialWorker();
        worker.ApplyProjection(member, 1, Guid.NewGuid(), true, "Synthetic", "Worker",
            Guid.NewGuid().ToString("N"), "Team", "T");
        var socialCase = new SocialCase(client, DateTime.Today.AddDays(-5), worker, "legacy note");
        context.Add(socialCase);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return (client.Id, socialCase.Id, worker.Id);
    }

    private async Task EndCase(Guid org, int caseId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        await using var command = new NpgsqlCommand(
            "UPDATE public.\"SocialCases\" SET \"EndDate\" = @end WHERE \"SchoolRegistrationId\" = @id",
            connection, tx);
        command.Parameters.AddWithValue("end", DateTime.Today.AddDays(-1));
        command.Parameters.AddWithValue("id", caseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await tx.CommitAsync();
    }

    private async Task SoftDeleteCase(Guid org, int caseId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        await using var command = new NpgsqlCommand(
            "UPDATE public.\"SocialCases\" SET \"Softdelete\" = true WHERE \"SchoolRegistrationId\" = @id",
            connection, tx);
        command.Parameters.AddWithValue("id", caseId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        await tx.CommitAsync();
    }

    private async Task<int> AddCase(Guid org, int clientId, int workerId)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await context.Database.UseTransactionAsync(tx);
        var client = await context.Clients.SingleAsync(x => x.Id == clientId);
        var worker = await context.SocialWorkers.SingleAsync(x => x.Id == workerId);
        var socialCase = new SocialCase(client, DateTime.Today.AddDays(-2), worker, "new legacy note");
        context.SocialCases.Add(socialCase);
        await context.SaveChangesAsync();
        await tx.CommitAsync();
        return socialCase.Id;
    }

    private async Task SeedInteractions(Guid org, int clientId, int caseId, Guid member, int count)
    {
        await using var connection = new NpgsqlConnection(database.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, org);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        await context.Database.UseTransactionAsync(tx);
        for (var i = 0; i < count; i++) context.Interactions.Add(new Interaction
        {
            ClientId = clientId, SocialCaseId = caseId, AuthorMembershipId = member,
            OccurredAtUtc = UtcMilliseconds().AddMinutes(-i), Note = $"Synthetic note {i}"
        });
        await context.SaveChangesAsync();
        await tx.CommitAsync();
    }

    private async Task<int> InteractionCase(Guid org, int interactionId)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.AdministratorConnectionString).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        return (await context.Interactions.SingleAsync(x => x.Id == interactionId)).SocialCaseId;
    }

    private async Task<int> Count<T>(Guid org) where T : class
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(database.AdministratorConnectionString).Options;
        await using var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(org));
        return await context.Set<T>().CountAsync();
    }

    private static async Task WaitForAdvisoryWaiter(NpgsqlConnection observer, int holderPid,
        Task<HttpResponseMessage> pending)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand("""
                SELECT waiter.pid
                FROM pg_catalog.pg_locks AS holder
                JOIN pg_catalog.pg_locks AS waiter
                  ON waiter.locktype = holder.locktype
                  AND waiter.database IS NOT DISTINCT FROM holder.database
                  AND waiter.classid = holder.classid
                  AND waiter.objid = holder.objid
                  AND waiter.objsubid = holder.objsubid
                JOIN pg_catalog.pg_stat_activity AS activity ON activity.pid = waiter.pid
                WHERE holder.pid = @holder_pid AND holder.locktype = 'advisory' AND holder.granted
                  AND waiter.pid <> holder.pid AND NOT waiter.granted
                  AND activity.wait_event_type = 'Lock' AND activity.wait_event = 'advisory'
                LIMIT 1
                """, observer);
            command.Parameters.AddWithValue("holder_pid", holderPid);
            if (await command.ExecuteScalarAsync() is int)
            {
                Assert.False(pending.IsCompleted);
                return;
            }
            Assert.False(pending.IsCompleted); // A completed request never reached the observed lock wait.
            await Task.Delay(25);
        }
        throw new TimeoutException("B1 write did not reach the PostgreSQL advisory-lock wait.");
    }

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction tx, Guid org)
    {
        await using var command = new NpgsqlCommand(
            "SELECT set_config('zeka.organisation_id', @organisation, true)", connection, tx);
        command.Parameters.AddWithValue("organisation", org.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class RequestIdentity(IHttpContextAccessor context) : IOperationIdentity
    {
        public string SubjectId => context.HttpContext?.Request.Headers["X-Test-Subject"].ToString() ?? string.Empty;
        public Guid SelectedOrganisationId => Guid.TryParse(
            context.HttpContext?.Request.Headers["X-Organisation-Id"].ToString(), out var value) ? value : Guid.Empty;
    }

    private sealed class Access : ICurrentTenantAccess
    {
        private readonly Dictionary<string, (Guid Org, Guid Member, string[] Permissions)> grants = new();
        private readonly HashSet<string> outages = new();
        public void Grant(string subject, Guid org, Guid member, params string[] permissions) =>
            grants[subject] = (org, member, permissions);
        public void Revoke(string subject) => grants.Remove(subject);
        public void Unavailable(string subject) => outages.Add(subject);
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken token)
        {
            if (outages.Contains(subject))
                return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Unavailable));
            if (!grants.TryGetValue(subject, out var grant) || grant.Org != selected)
                return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Denied));
            return Task.FromResult(new TenantAccessDecision(TenantAccessOutcome.Authorized,
                new AuthorizedTenantMembership(grant.Org, grant.Member, grant.Permissions,
                    "issue83-synthetic", DateTimeOffset.UtcNow)));
        }
    }

    private sealed class AmbiguousCommitProbe
    {
        public int OrdinaryAttempts;
    }

    private sealed class AmbiguousOnceExecutor(ITenantTransactionExecutor inner, AmbiguousCommitProbe probe)
        : ITenantTransactionExecutor
    {
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            inner.ExecuteAsync(work, token);
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            inner.ExecuteAsync(work, token);
        public Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            inner.ExecuteOrdinaryAsync(work, token);
        public Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            inner.ExecuteOrdinaryAsync(work, token);
        public Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            inner.ExecuteLifecycleAsync(work, token);
        public Task ExecuteLifecycleAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            inner.ExecuteLifecycleAsync(work, token);
        public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            inner.ExecuteOnceAsync(work, token);
        public async Task<T> ExecuteOrdinaryOnceAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken token)
        {
            var result = await inner.ExecuteOrdinaryOnceAsync(work, token);
            if (Interlocked.Increment(ref probe.OrdinaryAttempts) == 1)
                throw new TenantCommitIndeterminateException(new Exception("synthetic lost acknowledgement"));
            return result;
        }
    }
}
