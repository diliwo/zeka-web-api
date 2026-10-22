using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthManager.API;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ocelot.Configuration;
using Ocelot.Configuration.File;
using Ocelot.DependencyInjection;
using Ocelot.Configuration.Repository;
using Ocelot.Middleware;
using Ocelot.Requester;
using Zeka.Authentication.Tests;
using Zeka.Gateway;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "89")]
[Trait("Slice", "GatewayBrowserTransport")]
public sealed class GatewayBrowserTransportTests(PostgreSqlFixture postgres, ITestOutputHelper output)
{
    private const string Origin = "https://browser.example.invalid";
    private const string Session = "/api/v1/browser-session/";
    private const string Eligible = "/api/v1/browser-organisations/eligible";
    private const string Password = "Synthetic-Only!89";

    [Theory]
    [InlineData("ada", 2)]
    [InlineData("ben", 1)]
    public async Task Real_ocelot_provider_flow_keeps_choices_distinct_from_live_access_and_revokes_session(string user, int choices)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Real_ocelot_provider_flow_keeps_choices_distinct_from_live_access_and_revokes_session), new object?[] { user, choices }, output)) return;
        await using var host = await StartAsync();
        var token = await host.SignInAsync(user);
        using (var list = await host.GetAsync(Eligible, token))
        {
            await Success(list, HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
            Assert.Equal(choices, json.RootElement.GetProperty("organisations").GetArrayLength());
            Assert.Equal(new[] { "contractVersion", "subjectId", "organisations" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        }
        using (var selected = await host.GetAsync("/api/v1/tenant-access/" + host.Beta, token))
        {
            await Success(selected, HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
            Assert.Equal(host.Beta, json.RootElement.GetProperty("organisationId").GetGuid());
            Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("organisationMembershipId").GetGuid());
            Assert.NotEmpty(json.RootElement.GetProperty("effectivePermissionCodes").EnumerateArray());
        }
        using (var denied = await host.GetAsync("/api/v1/tenant-access/" + Guid.NewGuid(), token))
            await Error(denied, HttpStatusCode.Forbidden);
        // A choice is not a cached access grant: remove the membership after discovery.
        await using (var db = host.Administrator())
        {
            var membership = await db.OrganisationMemberships.SingleAsync(x => x.OrganisationId == host.Beta && x.UserId == host.Users[user]);
            db.Entry(membership).Property(x => x.Status).CurrentValue = MembershipStatus.Left;
            await db.SaveChangesAsync();
        }
        using (var denied = await host.GetAsync("/api/v1/tenant-access/" + host.Beta, token)) await Error(denied, HttpStatusCode.Forbidden);
        var before = host.RefreshCookie;
        using (var restored = await host.PostAsync("restore", await host.CsrfAsync()))
        {
            await Success(restored, HttpStatusCode.OK);
            AssertCookie(restored, BrowserSessionEndpoints.CookieName);
            Assert.NotEqual(before, host.RefreshCookie);
            token = await Token(restored);
        }
        using (var logout = await host.PostAsync("logout", await host.CsrfAsync()))
        {
            await Success(logout, HttpStatusCode.NoContent);
            Assert.Contains("expires=", logout.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        }
        using (var denied = await host.GetAsync(Eligible, token)) await Error(denied, HttpStatusCode.Unauthorized);
        using (var denied = await host.GetAsync("/api/v1/tenant-access/" + host.Alpha, token)) await Error(denied, HttpStatusCode.Unauthorized);
        Assert.Equal(1, host.Received.Count(x => x.Path == Session + "restore"));
        Assert.Equal(1, host.Received.Count(x => x.Path == Session + "logout"));
        Assert.All(host.Received, x => Assert.Equal("127.0.0.2", x.Client));
        host.AssertRedacted(token);
        output.WriteLine($"real_ocelot=true provider=PostgreSQL choices={choices} live_denial=true rotation=true logout_revocation=true final_assertions=true");
    }

    [Fact]
    public async Task Exact_route_and_credential_matrix_exposes_only_six_method_path_pairs()
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Exact_route_and_credential_matrix_exposes_only_six_method_path_pairs), new object?[] {  }, output)) return;
        await using var host = await StartAsync();
        var token = await host.SignInAsync("ada");
        var paths = new[] { Session + "csrf", Session + "sign-in", Session + "restore", Session + "logout", Eligible, "/api/v1/tenant-access/" + host.Alpha };
        for (var index = 0; index < paths.Length; index++)
        {
            var right = index is 1 or 2 or 3 ? HttpMethod.Post : HttpMethod.Get;
            foreach (var wrong in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head }.Where(x => x != right))
            {
                using var response = await host.ObserveOrdinaryAsync($"method-{index}-{wrong.Method}", paths[index], output, wrong);
                await Error(response, HttpStatusCode.NotFound);
            }
            var variantIndex = 0;
            foreach (var path in new[] { paths[index] + "/", paths[index] + "/extra", paths[index].ToUpperInvariant() })
            {
                using var response = await host.ObserveOrdinaryAsync($"path-{index}-{++variantIndex}", path, output);
                await Error(response, HttpStatusCode.NotFound);
            }
        }
        var invalidIndex = 0;
        foreach (var path in new[] { "/api/v1/browser-session", "/api/v1/browser-session/unknown", "/api/v1/tenant-access/not-a-guid",
            "/api/v1/tenant-access/", "/api/v1/tenant-access/" + Guid.Empty, "/api/v1/tenant-access/" + host.Alpha + "/memberships/" + Guid.NewGuid(), "/api/v1/internal", "/api/v1/browser-session/%63srf" })
        {
            var caseId = "invalid-" + ++invalidIndex;
            if (path == Session + "%63srf")
                AssertLiteralDenied(await host.ObserveLiteralAsync(caseId, path, output));
            else
            {
                using var response = await host.ObserveOrdinaryAsync(caseId, path, output);
                await Error(response, HttpStatusCode.NotFound);
            }
        }
        foreach (var path in new[] { Eligible, "/api/v1/tenant-access/" + host.Alpha })
        {
            using (var response = await host.GetAsync(path)) await Error(response, HttpStatusCode.Unauthorized);
            using (var response = await host.GetAsync(path, "not-a-token")) await Error(response, HttpStatusCode.Unauthorized);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, Eligible))
        {
            request.Headers.Authorization = new("Bearer", token); request.Headers.Add("X-Organisation-Id", host.Alpha.ToString());
            using var response = await host.Http.SendAsync(request); await Error(response, HttpStatusCode.BadRequest);
        }
        using (var response = await host.GetAsync(Eligible + "?organisationId=" + host.Alpha, token)) await Error(response, HttpStatusCode.BadRequest);
        foreach (var origin in new string?[] { null, "https://unapproved.example.invalid" })
        {
            using var response = await host.PostAsync("restore", await host.CsrfAsync(), origin: origin); await Error(response, HttpStatusCode.Forbidden);
        }
        using (var response = await host.PostAsync("restore", "invalid-csrf")) await Error(response, HttpStatusCode.Forbidden);
        using (var response = await host.PostAsync("restore", await host.CsrfAsync(), body: "{}", contentType: "text/plain")) await Error(response, HttpStatusCode.Forbidden);
        using var noCookies = host.Client("127.0.0.2");
        using (var response = await host.PostAsync("restore", await host.CsrfAsync(noCookies), client: noCookies)) await Error(response, HttpStatusCode.Unauthorized, allowCookie: true);
        using (var response = await host.PostAsync("logout", await host.CsrfAsync(noCookies), client: noCookies)) await Error(response, HttpStatusCode.Unauthorized);
        // Existing legacy route templates are untouched and still participate in the actual gateway.
        await host.AssertLegacyForwardingAsync(output);
        Assert.Contains(host.Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes, x => x.UpstreamPathTemplate == "/login");
        output.WriteLine("six_method_path_matrix=true origin_csrf_and_credential_negatives=true internal_routes_closed=true final_assertions=true");
    }

    [Fact]
    public async Task Ordinary_URI_normalization_and_literal_TLS_target_are_observed_at_the_unchanged_gateway()
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Ordinary_URI_normalization_and_literal_TLS_target_are_observed_at_the_unchanged_gateway), new object?[] {  }, output)) return;
        await using var host = await StartAsync();
        const string encoded = "/api/v1/browser-session/%63srf";
        using (var ordinary = await host.ObserveOrdinaryAsync("normalization-control", encoded, output))
        {
            // This is the observed canonical request control, not the disallowed-target assertion.
            await Success(ordinary, HttpStatusCode.OK);
            Assert.Equal(Session + "csrf", ordinary.RequestMessage!.RequestUri!.PathAndQuery);
        }
        var literal = await host.ObserveLiteralAsync("literal-encoded", encoded, output);
        AssertLiteralDenied(literal);
        var control = await host.ObserveLiteralAsync("literal-canonical-control", Session + "csrf", output);
        Assert.Equal(HttpStatusCode.OK, control.Status);
        Assert.True(control.NoStore);
        Assert.True(control.ReachedAuthManager);
        output.WriteLine("ordinary_normalization_proved=true literal_encoded_denied=true literal_canonical_control_passed=true final_assertions=true");
    }

    private static void AssertLiteralDenied(LiteralObservation observed)
    {
        Assert.Equal(HttpStatusCode.NotFound, observed.Status);
        Assert.True(observed.NoStore);
        Assert.Equal(0L, observed.ContentLength);
        Assert.False(observed.HasSetCookie);
        Assert.False(observed.ReachedAuthManager);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Owned_legacy_listener_is_disposed_on_success_and_startup_failure(bool failStartup)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Owned_legacy_listener_is_disposed_on_success_and_startup_failure), new object?[] { failStartup }, output)) return;
        WebApplication? listener = null;
        var original = new InvalidOperationException("Synthetic fixture startup failure after legacy acquisition");
        if (failStartup)
        {
            var error = await Record.ExceptionAsync(() => StartAsync(configure: _ => throw original, legacyAcquired: app => listener = app));
            Assert.Same(original, error);
        }
        else
        {
            var host = await StartAsync(legacyAcquired: app => listener = app);
            await host.DisposeAsync();
        }
        Assert.NotNull(listener);
        Assert.Throws<ObjectDisposedException>(() => listener!.Services.GetService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        output.WriteLine($"legacy_listener_disposed=true startup_failure={failStartup} original_failure_preserved=true final_assertions=true");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Exact_json_headers_cookies_and_framing_cross_real_ocelot(bool knownLength)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Exact_json_headers_cookies_and_framing_cross_real_ocelot), new object?[] { knownLength }, output)) return;
        await using var host = await StartAsync();
        var csrf = await host.CsrfAsync();
        var body = "{\"email\":\"ada.gateway@example.invalid\",\"password\":\"" + Password + "\"}";
        using var response = await host.PostAsync("sign-in", csrf, body: body, knownLength: knownLength);
        await Success(response, HttpStatusCode.OK); AssertCookie(response, BrowserSessionEndpoints.CookieName);
        var received = Assert.Single(host.Received.Where(x => x.Path == Session + "sign-in"));
        Assert.Equal(body, received.Body); Assert.Equal("application/json; charset=utf-8", received.ContentType);
        Assert.Equal(Origin, received.Origin); Assert.Equal(csrf, received.Csrf); Assert.Contains("__Secure-ZekaCSRF=", received.Cookie);
        Assert.Equal(knownLength, received.Length.HasValue); Assert.Equal(!knownLength, received.Chunked);
        using var selected = await host.GetAsync(Eligible, await Token(response)); await Success(selected, HttpStatusCode.OK);
        Assert.StartsWith("Bearer ", host.Received.Last().Authorization);
        Assert.DoesNotContain(BrowserSessionEndpoints.CookieName, host.Received.Last().Cookie); // browser path scoping
        output.WriteLine($"real_ocelot=true known_length={knownLength} exact_bytes_headers=true cookie_attributes=true final_assertions=true");
    }

    [Fact]
    public async Task Attribution_is_exact_peer_fail_closed_and_backend_budgets_remain_independent_and_bounded()
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Attribution_is_exact_peer_fail_closed_and_backend_budgets_remain_independent_and_bounded), new object?[] {  }, output)) return;
        await using var host = await StartAsync(perAddress: 3, aggregate: 30, partitions: 4);
        foreach (var value in new[] { "", "not-an-address", "198.51.100.1, 198.51.100.2", "198.51.100.1" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Session + "csrf");
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", value);
            using var response = await host.Http.SendAsync(request); await Error(response, HttpStatusCode.ServiceUnavailable);
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, Session + "csrf"))
        {
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", new[] { "198.51.100.1", "198.51.100.2" });
            using var response = await host.Http.SendAsync(request); await Error(response, HttpStatusCode.ServiceUnavailable);
        }
        foreach (var name in new[] { "Forwarded", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Unknown", "X-Original-For" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Session + "csrf"); request.Headers.Add(name, "forged");
            using var response = await host.Http.SendAsync(request); await Error(response, HttpStatusCode.ServiceUnavailable);
        }
        using var untrusted = host.Client("127.0.0.9");
        using (var response = await untrusted.GetAsync(Session + "csrf")) await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Empty(host.Received); Assert.Equal(0, host.Admission.PartitionCount);
        using var bypass = host.DirectClient();
        using (var response = await bypass.GetAsync(Session + "csrf")) await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(0, host.Admission.PartitionCount);
        var csrf = await host.CsrfAsync();
        for (var i = 0; i < 2; i++) using (await host.Http.GetAsync(Session + "csrf")) { }
        using (var exhausted = await host.Http.GetAsync(Session + "csrf")) await Error(exhausted, HttpStatusCode.TooManyRequests);
        using (var mutation = await host.PostAsync("sign-in", csrf)) await Success(mutation, HttpStatusCode.OK);
        using var shared = host.Client("127.0.0.2");
        using (var response = await shared.GetAsync(Session + "csrf")) await Error(response, HttpStatusCode.TooManyRequests);
        using var other = host.Client("127.0.0.3");
        using (var response = await other.GetAsync(Session + "csrf")) await Success(response, HttpStatusCode.OK);
        for (var peer = 4; peer <= 7; peer++)
        { using var client = host.Client("127.0.0." + peer); using var response = await client.GetAsync(Session + "csrf"); }
        Assert.Equal(5, host.Admission.PartitionCount); // four CSRF keys plus one mutation key
        using var last = host.Client("127.0.0.8");
        using (var response = await last.GetAsync(Session + "csrf")) await Error(response, HttpStatusCode.TooManyRequests);
        Assert.Equal(1, host.Received.Count(x => x.Path == Session + "sign-in"));
        output.WriteLine("spoofing_untrusted_and_direct_bypass_denied=true distinct_and_shared_addresses=true separate_budgets=true bounded_keys=true final_assertions=true");
    }

    [Fact]
    public async Task Aggregate_budget_does_not_replace_per_address_admission()
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Aggregate_budget_does_not_replace_per_address_admission), new object?[] {  }, output)) return;
        await using var host = await StartAsync(perAddress: 2, aggregate: 2);
        using (var response = await host.Http.GetAsync(Session + "csrf")) await Success(response, HttpStatusCode.OK);
        using var second = host.Client("127.0.0.3"); using var third = host.Client("127.0.0.4");
        using (var response = await second.GetAsync(Session + "csrf")) await Success(response, HttpStatusCode.OK);
        using (var response = await third.GetAsync(Session + "csrf")) await Error(response, HttpStatusCode.TooManyRequests);
        Assert.DoesNotContain(host.Received, x => x.Path == Session + "sign-in");
    }

    [Theory]
    [InlineData("GatewayBrowserTransport:Origin", null)]
    [InlineData("GatewayBrowserTransport:Origin", "http://browser.example.invalid")]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", null)]
    [InlineData("GatewayBrowserTransport:DownstreamOrigin", "https://target.example.invalid/prefix")]
    [InlineData("GatewayBrowserTransport:ClientPeers:0", "invalid")]
    public async Task Missing_or_invalid_required_configuration_disables_routes_without_legacy_fallback(string key, string? value)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Missing_or_invalid_required_configuration_disables_routes_without_legacy_fallback), new object?[] { key, value }, output)) return;
        await using var host = await StartAsync(configure: values => values[key] = value);
        using var response = await host.Http.GetAsync(Session + "csrf"); await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Empty(host.Received);
        Assert.DoesNotContain(host.Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes,
            route => route.Key?.StartsWith("GatewayBrowserTransport:") == true);
    }

    [Fact]
    public async Task Unavailable_database_overflow_and_downstream_emit_generic_no_store_without_cached_choices()
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Unavailable_database_overflow_and_downstream_emit_generic_no_store_without_cached_choices), new object?[] {  }, output)) return;
        await using var host = await StartAsync(); var token = await host.SignInAsync("ada");
        using (var response = await host.GetAsync(Eligible, token)) await Success(response, HttpStatusCode.OK);
        host.DatabaseFailure.Armed = true;
        using (var response = await host.GetAsync(Eligible, token)) await Error(response, HttpStatusCode.ServiceUnavailable);
        using (var response = await host.PostAsync("logout", await host.CsrfAsync())) await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.NotNull(host.RefreshCookie);
        host.DatabaseFailure.Armed = false;
        await using (var db = host.Administrator())
        {
            for (var i = 0; i < 100; i++) AddChoice(db, host.Users["ada"], "Overflow " + i);
            await db.SaveChangesAsync();
        }
        using (var response = await host.GetAsync(Eligible, token)) await Error(response, HttpStatusCode.Conflict);
        await host.Backend.StopAsync();
        using (var response = await host.GetAsync(Eligible, token)) await Error(response, HttpStatusCode.ServiceUnavailable);
        host.AssertRedacted(token);
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("logout")]
    public async Task Lost_mutation_response_is_not_transparently_replayed_and_requires_explicit_recovery(string action)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Lost_mutation_response_is_not_transparently_replayed_and_requires_explicit_recovery), new object?[] { action }, output)) return;
        await using var host = await StartAsync(lostResponse: true); var token = await host.SignInAsync("ada");
        var cookie = host.RefreshCookie;
        host.Pool.DropPath = Session + action;
        using (var response = await host.PostAsync(action, await host.CsrfAsync())) await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Equal(1, host.Received.Count(x => x.Path == Session + action));
        Assert.Equal(cookie, host.RefreshCookie);
        using (var retry = await host.PostAsync(action, await host.CsrfAsync()))
        {
            if (action == "restore") await Error(retry, HttpStatusCode.Unauthorized, allowCookie: true);
            else await Success(retry, HttpStatusCode.NoContent);
        }
        Assert.Equal(2, host.Received.Count(x => x.Path == Session + action));
        using (var denied = await host.GetAsync(Eligible, token)) await Error(denied, HttpStatusCode.Unauthorized);
        output.WriteLine($"lost_{action}_response=true sent_once=true explicit_fresh_csrf_recovery=true old_session_denied=true final_assertions=true");
    }


    [Fact]
    public async Task Isolated_gateway_case_and_real_handler_controls()
    {
        var mode = Environment.GetEnvironmentVariable(GatewayLinuxValidation.Mode);
        if (mode == "browser")
        {
            var input = GatewayLinuxValidation.ReadCase();
            var method = typeof(GatewayBrowserTransportTests).GetMethod(input.Method)!;
            GatewayLinuxValidation.Check(method is not null && method.Name != nameof(Isolated_gateway_case_and_real_handler_controls), "child-method");
            var parameters = method!.GetParameters();
            var args = input.Arguments.Select((value, index) => value.Deserialize(parameters[index].ParameterType)).ToArray();
            try { await (Task)method.Invoke(this, args)!; }
            catch { throw new InvalidOperationException("isolated-browser-assertion-failed"); }
        }
        else if (mode == "transport") await GatewayLinuxValidation.TransportChildAsync();
        else await GatewayLinuxValidation.ProveControlsAsync(output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Safe_legacy_diagnostics_are_attributable_with_profile_on_and_off(bool enabled)
    {
        if (await GatewayLinuxValidation.RunCaseAsync(nameof(Safe_legacy_diagnostics_are_attributable_with_profile_on_and_off), new object?[] { enabled }, output)) return;
        await using var host = await StartAsync(configure: values => { if (!enabled) values["GatewayBrowserTransport:Origin"] = null; });
        var before = host.Logs.Events.Count;
        await host.AssertLegacyForwardingAsync(output);
        var events = host.Logs.Events.Skip(before).Where(e => e.Category == BrowserTransport.DiagnosticCategory && e.Event == 8901).ToArray();
        var completed = Assert.Single(events);
        Assert.Contains("route=legacy-clients", completed.Text); Assert.Contains("status=404", completed.Text);
        Assert.Contains("dependency=none", completed.Text); Assert.Contains("correlation=", completed.Text);
        var marker = "synthetic-private-diagnostic-" + Guid.NewGuid().ToString("N");
        host.Gateway.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Unexpected.Raw.Source").LogError(new Exception(marker), "{Private}", marker);
        GatewayLinuxValidation.RequireNoLeaks(host.Logs.Messages, new[] { marker });
        await host.Legacy.StopAsync();
        using var unavailable = await host.Http.GetAsync("/clients/search");
        Assert.True((int)unavailable.StatusCode >= 500);
        Assert.Contains(host.Logs.Events, e => e.Category == BrowserTransport.DiagnosticCategory && e.Event == 8901 && e.Text.Contains("route=legacy-clients") && e.Text.Contains("dependency=unavailable"));
        host.AssertRedacted("synthetic-unused-bearer");
    }

    private static async Task Success(HttpResponseMessage response, HttpStatusCode status)
    { Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); if (status == HttpStatusCode.NoContent) Assert.Equal("", await response.Content.ReadAsStringAsync()); }
    private static async Task Error(HttpResponseMessage response, HttpStatusCode status, bool allowCookie = false)
    { await Success(response, status); Assert.Equal("", await response.Content.ReadAsStringAsync()); if (!allowCookie) Assert.False(response.Headers.Contains("Set-Cookie")); }
    private static void AssertCookie(HttpResponseMessage response, string name)
    {
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie").Where(value => value.StartsWith(name + "=")));
        foreach (var flag in new[] { "secure", "httponly", "samesite=strict", "path=/api/v1/browser-session" }) Assert.Contains(flag, cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<string> Token(HttpResponseMessage response)
    { using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("accessToken").GetString()!; }
    private static Guid AddChoice(AuthDbContext db, Guid user, string name)
    {
        var now = DateTimeOffset.UtcNow; var org = Organisation.Create(Guid.NewGuid(), name, user, now);
        db.Add(org); db.Entry(org).Property(x => x.Status).CurrentValue = OrganisationStatus.Active;
        db.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), org.Id, user, now)); return org.Id;
    }

    private async Task<Fixture> StartAsync(int perAddress = 60, int aggregate = 256, int partitions = 16,
        Action<Dictionary<string, string?>>? configure = null, Action<WebApplication>? legacyAcquired = null, bool lostResponse = false)
    {
        var owned = new Fixture();
        try
        {
            owned.AdminConnection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Pooling = false }.ConnectionString;
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "ApiGateways/Ocelot.ApiGateway/ocelot.json"))) root = root.Parent;
            Assert.NotNull(root);
            var bootstrap = await File.ReadAllTextAsync(Path.Combine(root!.FullName, "Deployments/database/bootstrap-auth-roles.sql"));
            async Task Bootstrap()
            {
                await using var sql = new NpgsqlConnection(owned.AdminConnection); await sql.OpenAsync();
                await using var command = new NpgsqlCommand(bootstrap, sql); await command.ExecuteNonQueryAsync();
            }
            await Bootstrap();
            await using (var db = owned.Administrator())
            { await db.Database.OpenConnectionAsync(); await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner"); await db.Database.MigrateAsync(); }
            await Bootstrap();
            await using (var sql = new NpgsqlConnection(owned.AdminConnection))
            {
                await sql.OpenAsync(); await using var command = new NpgsqlCommand("ALTER ROLE zeka_auth_runtime PASSWORD 'fictional-discovery-runtime'", sql); await command.ExecuteNonQueryAsync();
            }
            var runtime = new NpgsqlConnectionStringBuilder(owned.AdminConnection) { Username = "zeka_auth_runtime", Password = "fictional-discovery-runtime", Pooling = false }.ConnectionString;
            owned.Auth = new AuthenticationFixture();
            owned.KeyPath = Path.Combine(Path.GetTempPath(), "fictional-gateway-" + Guid.NewGuid().ToString("N") + ".key");
            await File.WriteAllBytesAsync(owned.KeyPath, RandomNumberGenerator.GetBytes(32));
            owned.Certificate = GatewayLinuxValidation.BrowserCertificate();
            var backend = WebApplication.CreateBuilder(); backend.Logging.ClearProviders();
            backend.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(owned.Certificate)));
            backend.Configuration.AddConfiguration(owned.Auth.Configuration());
            backend.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BrowserSession:Origin"] = Origin, ["BrowserSession:TrustedProxyPeers:0"] = "127.0.0.1",
                ["BrowserSession:Limits:PerAddress"] = perAddress.ToString(), ["BrowserSession:Limits:Aggregate"] = aggregate.ToString(),
                ["BrowserSession:Limits:MaxPartitions"] = partitions.ToString(), ["BrowserSession:Limits:WindowSeconds"] = "60"
            });
            backend.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(runtime, provider => provider.EnableRetryOnFailure()).AddInterceptors(owned.DatabaseFailure));
            backend.ConfigureMicrosoftIdentity(); backend.Services.AddOnboardingPersistenceFoundations(backend.Configuration);
            backend.Services.AddTenantAuthentication(backend.Configuration);
            backend.Services.AddSingleton<Zeka.Authentication.IJwksDocumentSource>(new DocumentSource(owned.Auth.Jwks()));
            backend.Services.AddSingleton<TimeProvider>(owned.Auth.Clock);
            var issuer = IssuerContractTests.Create(owned.Auth); issuer.ConfirmPublication(await issuer.PrepareAsync());
            backend.Services.AddSingleton<ISessionAccessTokenIssuer>(issuer);
            backend.Services.AddSingleton(new BrowserSessionPolicy { HashKeyReference = owned.KeyPath, AccessLifetime = TimeSpan.FromMinutes(5), IdleLifetime = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromDays(7) });
            backend.Services.AddScoped<IBrowserSessions, BrowserSessions>();
            backend.Services.AddScoped<IEligibleOrganisations, EligibleOrganisationsResolver>();
            backend.Services.AddScoped<ICurrentTenantAccess, CurrentTenantAccessResolver>();
            backend.Services.AddScoped<IActiveOrganisationMembership, ActiveOrganisationMembership>();
            backend.Services.AddDataProtection().UseEphemeralDataProtectionProvider(); backend.Services.AddBrowserSessionTransport();
            owned.Backend = backend.Build();
            owned.Backend.Use(async (http, next) =>
            {
                var correlation = http.Request.Headers["X-Zeka-Test-Case"].ToString();
                if (owned.PathRequests.ContainsKey(correlation)) owned.DownstreamPaths[correlation] = http.Request.Path.Value!;
                http.Request.EnableBuffering(); using var reader = new StreamReader(http.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(); http.Request.Body.Position = 0;
                owned.Received.Enqueue(new(http.Request.Path, body, http.Request.ContentType, http.Request.Headers.Origin.ToString(),
                    http.Request.Headers["X-Zeka-CSRF"].ToString(), http.Request.Headers.Cookie.ToString(), http.Request.Headers.Authorization.ToString(),
                    http.Request.Headers["X-Forwarded-For"].ToString(), http.Request.ContentLength, http.Request.Headers.TransferEncoding.Contains("chunked")));
                await next(http);
            });
            owned.Backend.UseBrowserSessionAttribution(); owned.Backend.UseRateLimiter(); owned.Backend.UseAuthentication(); owned.Backend.UseAuthorization();
            owned.Backend.MapBrowserSessions(); owned.Backend.MapBrowserEligibleOrganisations(); owned.Backend.MapTenantAccess();
            await owned.Backend.StartAsync();
            using (var scope = owned.Backend.Services.CreateScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<User>>();
                foreach (var name in new[] { "ada", "ben" })
                {
                    var user = User.Create(name + ".gateway@example.invalid", name + ".gateway", name, "Fictional", owned.Auth.Clock.GetUtcNow()); user.EmailConfirmed = true;
                    Assert.True((await manager.CreateAsync(user, Password)).Succeeded); owned.Users[name] = user.Id;
                    await using var db = owned.Administrator(); var persisted = await db.Users.SingleAsync(x => x.Id == user.Id);
                    db.Entry(persisted).Property(x => x.Status).CurrentValue = UserStatus.Active; await db.SaveChangesAsync();
                }
            }
            await using (var db = owned.Administrator())
            {
                owned.Alpha = AddChoice(db, owned.Users["ada"], "Alpha"); owned.Beta = AddChoice(db, owned.Users["ada"], "Beta");
                db.Add(OrganisationMembership.Create(Guid.NewGuid(), owned.Beta, owned.Users["ben"], PermissionSet.ViewerId, DateTimeOffset.UtcNow)); await db.SaveChangesAsync();
            }
            var legacyBuilder = WebApplication.CreateBuilder(); legacyBuilder.Logging.ClearProviders();
            legacyBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            owned.Legacy = legacyBuilder.Build();
            owned.Legacy.Run(http =>
            {
                var correlation = http.Request.Headers["X-Zeka-Test-Case"].ToString();
                var expected = http.Request.Method == "GET" && http.Request.Path == "/api/clients/search";
                http.Response.StatusCode = expected ? 404 : 400;
                owned.LegacyReceipts.Enqueue(new(correlation, http.Request.Method, http.Request.Path.Value!,
                    http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget, http.Response.StatusCode));
                http.Response.Headers["X-Zeka-Legacy-Fixture"] = correlation;
                return Task.CompletedTask;
            });
            await owned.Legacy.StartAsync(); legacyAcquired?.Invoke(owned.Legacy);
            var gateway = WebApplication.CreateBuilder(); gateway.Logging.ClearProviders(); gateway.Logging.AddProvider(owned.Logs);
            gateway.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(owned.Certificate)));
            gateway.Configuration.AddJsonFile(Path.Combine(root.FullName, "ApiGateways/Ocelot.ApiGateway/ocelot.json"), optional: false, reloadOnChange: false);
            var routes = gateway.Configuration.GetSection("Routes").Get<List<FileRoute>>()!;
            var legacyRoute = FindLegacyRoute(routes); // Fail setup unless the existing generic mapping is unique.
            Assert.Single(legacyRoute.DownstreamHostAndPorts);
            owned.LegacyRouteOptionsHash = RouteOptionsHash(legacyRoute);
            var legacyIndex = routes.IndexOf(legacyRoute);
            var destination = new Uri(Address(owned.Legacy));
            var values = new Dictionary<string, string?> { ["GatewayBrowserTransport:Origin"] = Origin, ["GatewayBrowserTransport:DownstreamOrigin"] = Address(owned.Backend) };
            values[$"Routes:{legacyIndex}:DownstreamHostAndPorts:0:Host"] = destination.Host;
            values[$"Routes:{legacyIndex}:DownstreamHostAndPorts:0:Port"] = destination.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (var peer = 2; peer <= 8; peer++) values["GatewayBrowserTransport:ClientPeers:" + (peer - 2)] = "127.0.0." + peer;
            configure?.Invoke(values); gateway.Configuration.AddInMemoryCollection(values); BrowserTransport.Add(gateway);
            // The replacement is confined to the pre-existing lost-response injection.
            if (lostResponse) gateway.Services.AddSingleton<IMessageInvokerPool>(_ => owned.Pool = new PinnedPool(owned.Certificate));
            owned.Gateway = gateway.Build();
            if (!lostResponse) Assert.IsType<MessageInvokerPool>(owned.Gateway.Services.GetRequiredService<IMessageInvokerPool>());
            // Observe before the real guard without rewriting any request feature or header.
            owned.Gateway.Use(async (http, next) =>
            {
                var correlation = http.Request.Headers["X-Zeka-Test-Case"].ToString();
                var raw = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()!.RawTarget;
                var path = http.Request.Path.Value!;
                try { await next(http); }
                finally
                {
                    if (owned.PathRequests.TryGetValue(correlation, out var observation))
                        observation.TrySetResult(new(raw, path, http.Request.Method, http.Response.StatusCode));
                }
            });
            owned.Gateway.UseBrowserTransport(); await owned.Gateway.UseOcelot(); await owned.Gateway.StartAsync();
            var configuredLegacy = FindLegacyRoute(owned.Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes);
            Assert.Equal(owned.LegacyRouteOptionsHash, RouteOptionsHash(configuredLegacy));
            Assert.Equal(destination.Host, Assert.Single(configuredLegacy.DownstreamHostAndPorts).Host);
            Assert.Equal(destination.Port, configuredLegacy.DownstreamHostAndPorts[0].Port);
            owned.Http = owned.Client("127.0.0.2"); return owned;
        }
        catch { await owned.DisposeOwnedAsync(suppressErrors: true); throw; }
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static FileRoute FindLegacyRoute(IEnumerable<FileRoute> routes) => Assert.Single(routes.Where(route =>
        route.UpstreamPathTemplate == "/clients/{url}" && route.DownstreamPathTemplate == "/api/clients/{url}"
        && route.DownstreamScheme == "http" && route.UpstreamHttpMethod.SequenceEqual(new[] { "GET" }, StringComparer.OrdinalIgnoreCase)));
    private static string RouteOptionsHash(FileRoute route)
    {
        var properties = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(route))!.AsObject();
        properties.Remove(nameof(FileRoute.DownstreamHostAndPorts));
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(properties)));
    }
    private sealed record Received(string Path, string Body, string? ContentType, string Origin, string Csrf, string Cookie, string Authorization, string Client, long? Length, bool Chunked);
    private sealed record GatewayPath(string RawTarget, string DecodedPath, string Method, int Status);
    private sealed record LiteralObservation(HttpStatusCode Status, bool NoStore, long? ContentLength, bool HasSetCookie, bool ReachedAuthManager);
    private sealed record LegacyReceipt(string Correlation, string Method, string Path, string RawTarget, int Status);
    private sealed class Failure : DbCommandInterceptor
    {
        internal bool Armed;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken token = default)
        { if (Armed) throw new NpgsqlException("Synthetic unavailable database"); return ValueTask.FromResult(result); }
    }
    private sealed class PinnedPool : IMessageInvokerPool, IDisposable
    {
        private readonly HttpMessageInvoker invoker;
        internal string? DropPath;
        internal int Calls;
        internal PinnedPool(X509Certificate2 certificate)
        {
            invoker = new HttpMessageInvoker(new LostResponse(this) { InnerHandler = new SocketsHttpHandler
            {
                UseCookies = false, UseProxy = false, AllowAutoRedirect = false,
                SslOptions = new() { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == certificate.Thumbprint }
            } });
        }
        public HttpMessageInvoker Get(DownstreamRoute route) { Calls++; return invoker; }
        public void Clear() { }
        public void Dispose() => invoker.Dispose();
        private sealed class LostResponse(PinnedPool owner) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                var response = await base.SendAsync(request, token);
                if (owner.DropPath == request.RequestUri!.AbsolutePath)
                { owner.DropPath = null; response.Dispose(); throw new HttpRequestException("Synthetic lost response after committed mutation"); }
                return response;
            }
        }
    }
    private sealed class LogCapture : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();
        internal ConcurrentQueue<(string Category, int Event, string Text)> Events { get; } = new();
        public ILogger CreateLogger(string category) => new Capture(this, category);
        public void Dispose() { }
        private sealed class Capture(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
            {
                var text = format(state, error) + (error is null ? "" : " " + error.ToString());
                owner.Messages.Enqueue(text); owner.Events.Enqueue((category, id.Id, text));
            }
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal WebApplication Backend = null!, Gateway = null!, Legacy = null!;
        internal AuthenticationFixture Auth = null!;
        internal X509Certificate2 Certificate = null!;
        internal HttpClient Http = null!;
        internal PinnedPool Pool = null!;
        internal Failure DatabaseFailure = new();
        internal LogCapture Logs = new();
        internal string AdminConnection = "", KeyPath = "";
        internal Guid Alpha, Beta;
        internal Dictionary<string, Guid> Users = new();
        internal ConcurrentQueue<Received> Received = new();
        internal ConcurrentDictionary<string, TaskCompletionSource<GatewayPath>> PathRequests = new();
        internal ConcurrentDictionary<string, string> DownstreamPaths = new();
        internal ConcurrentQueue<LegacyReceipt> LegacyReceipts = new();
        internal string LegacyRouteOptionsHash = "";
        private readonly List<HttpClient> clients = new();
        private readonly Dictionary<HttpClient, CookieContainer> cookies = new();
        private readonly HashSet<string> historical = new(StringComparer.Ordinal);
        private void Remember(string? value) { if (!string.IsNullOrEmpty(value)) historical.Add(value); }
        private sealed class Tracking(Fixture owner) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                owner.Remember(request.Headers.Authorization?.Parameter);
                if (request.Headers.TryGetValues("X-Zeka-CSRF", out var values)) foreach (var value in values) owner.Remember(value);
                var path = request.RequestUri!.AbsolutePath;
                const string selector = "/api/v1/tenant-access/";
                if (path.StartsWith(selector, StringComparison.Ordinal)) owner.Remember(path[selector.Length..]);
                return await owner.TrackAsync(await base.SendAsync(request, token));
            }
        }
        private async Task<HttpResponseMessage> TrackAsync(HttpResponseMessage response)
        {
            foreach (var jar in cookies.Values) foreach (Cookie cookie in jar.GetAllCookies()) Remember(cookie.Value);
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var key in new[] { "accessToken", "csrfToken" })
                    if (json.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String) Remember(value.GetString());
            }
            return response;
        }
        internal BrowserSessionEndpoints.BrowserSessionAdmission Admission => Backend.Services.GetRequiredService<BrowserSessionEndpoints.BrowserSessionAdmission>();
        internal string? RefreshCookie => cookies[Http].GetCookies(new Uri(Origin + Session))[BrowserSessionEndpoints.CookieName]?.Value;
        internal AuthDbContext Administrator() => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(AdminConnection).Options);
        internal HttpClient DirectClient() => Client("127.0.0.9", direct: true);
        internal async Task AssertLegacyForwardingAsync(ITestOutputHelper output)
        {
            var correlation = "route-legacy-search-" + Guid.NewGuid().ToString("N");
            var observed = new TaskCompletionSource<GatewayPath>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(PathRequests.TryAdd(correlation, observed));
            using var request = new HttpRequestMessage(HttpMethod.Get, "/clients/search");
            request.Headers.Add("X-Zeka-Test-Case", correlation);
            output.WriteLine("legacy_boundary=" + JsonSerializer.Serialize(new { correlation, boundary = "upstream-send", method = "GET", path = "/clients/search" }));
            HttpResponseMessage received;
            try { received = await Http.SendAsync(request); }
            catch
            {
                output.WriteLine("legacy_failure=" + JsonSerializer.Serialize(new { correlation,
                    gatewayObserved = observed.Task.IsCompletedSuccessfully,
                    downstreamReceipts = LegacyReceipts.Where(value => value.Correlation == correlation).ToArray(),
                    reachedAuthManager = DownstreamPaths.ContainsKey(correlation) }));
                throw;
            }
            using var response = received;
            var gateway = await observed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var receipt = Assert.Single(LegacyReceipts);
            output.WriteLine("legacy_receipt=" + JsonSerializer.Serialize(new
            {
                correlation, upstreamMethod = "GET", upstreamPath = "/clients/search", gateway.RawTarget,
                gateway.DecodedPath, downstream = "fixture-owned-ephemeral-loopback", receipt.Method,
                receipt.Path, downstreamRawTarget = receipt.RawTarget, downstreamStatus = receipt.Status,
                responseStatus = (int)response.StatusCode, routeOptionsUnchanged = LegacyRouteOptionsHash == RouteOptionsHash(
                    FindLegacyRoute(Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes)),
                reachedAuthManager = DownstreamPaths.ContainsKey(correlation)
            }));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(correlation, Assert.Single(response.Headers.GetValues("X-Zeka-Legacy-Fixture")));
            Assert.Equal(correlation, receipt.Correlation); Assert.Equal("GET", receipt.Method);
            Assert.Equal("/api/clients/search", receipt.Path); Assert.Equal("/api/clients/search", receipt.RawTarget);
            Assert.Equal(404, receipt.Status); Assert.Equal(404, gateway.Status);
            Assert.Equal("/clients/search", gateway.RawTarget); Assert.False(DownstreamPaths.ContainsKey(correlation));
        }
        internal async Task<HttpResponseMessage> ObserveOrdinaryAsync(string caseId, string input, ITestOutputHelper output, HttpMethod? method = null)
        {
            var correlation = "route-" + caseId + "-" + Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<GatewayPath>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(PathRequests.TryAdd(correlation, completion));
            using var request = new HttpRequestMessage(method ?? HttpMethod.Get, input) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            var constructed = request.RequestUri!.OriginalString;
            request.Headers.Add("X-Zeka-Test-Case", correlation);
            var response = await Http.SendAsync(request);
            var gateway = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine("route_observation=" + JsonSerializer.Serialize(new
            {
                caseId, correlation, sender = "ordinary-HttpClient", input, method = request.Method.Method, constructedRequestUri = constructed,
                requestUri = request.RequestUri!.AbsoluteUri, originalString = request.RequestUri.OriginalString,
                serializedTarget = request.RequestUri.PathAndQuery, gateway.RawTarget, gateway.DecodedPath,
                responseStatus = (int)response.StatusCode, gatewayStatus = gateway.Status,
                reachedAuthManager = DownstreamPaths.ContainsKey(correlation),
                downstreamPath = DownstreamPaths.GetValueOrDefault(correlation)
            }));
            Assert.Equal(request.RequestUri.PathAndQuery, gateway.RawTarget);
            Assert.Equal((int)response.StatusCode, gateway.Status);
            return response;
        }
        internal async Task<LiteralObservation> ObserveLiteralAsync(string caseId, string target, ITestOutputHelper output)
        {
            // Only fixed fictional path probes are admitted; no credentials, cookies or bodies enter this sender.
            if (target != Session + "%63srf" && target != Session + "csrf") throw new ArgumentException("Unexpected literal test target");
            var correlation = "route-" + caseId + "-" + Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<GatewayPath>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(PathRequests.TryAdd(correlation, completion));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Parse("127.0.0.2"), 0));
            await socket.ConnectAsync(IPAddress.Loopback, new Uri(Address(Gateway)).Port, deadline.Token);
            await using var network = new NetworkStream(socket, ownsSocket: false);
            await using var tls = new SslStream(network, leaveInnerStreamOpen: true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = new Uri(Origin).DnsSafeHost, ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == Certificate.Thumbprint
            }, deadline.Token);
            var line = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\n");
            var headers = Encoding.ASCII.GetBytes("Host: browser.example.invalid\r\nX-Zeka-Test-Case: " + correlation + "\r\nConnection: close\r\n\r\n");
            await tls.WriteAsync(line, deadline.Token); await tls.WriteAsync(headers, deadline.Token); await tls.FlushAsync(deadline.Token);
            using var reader = new StreamReader(tls, Encoding.ASCII, leaveOpen: true);
            var statusLine = await reader.ReadLineAsync(deadline.Token);
            Assert.NotNull(statusLine);
            var status = (HttpStatusCode)int.Parse(statusLine!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            var noStore = false; var hasCookie = false; long? contentLength = null;
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 } header)
            {
                if (header.StartsWith("Cache-Control:", StringComparison.OrdinalIgnoreCase)) noStore = header.Contains("no-store", StringComparison.OrdinalIgnoreCase);
                if (header.StartsWith("Set-Cookie:", StringComparison.OrdinalIgnoreCase)) hasCookie = true;
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) contentLength = long.Parse(header[15..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                // Values of all other headers and response bodies are deliberately not recorded.
            }
            var gateway = await completion.Task.WaitAsync(deadline.Token);
            var reached = DownstreamPaths.ContainsKey(correlation);
            output.WriteLine("route_observation=" + JsonSerializer.Serialize(new
            {
                caseId, correlation, sender = "literal-TLS-HTTP1", input = target, method = "GET",
                requestLineAscii = Encoding.ASCII.GetString(line), requestLineHex = Convert.ToHexString(line),
                gateway.RawTarget, gateway.DecodedPath, responseStatus = (int)status, gatewayStatus = gateway.Status,
                reachedAuthManager = reached, downstreamPath = DownstreamPaths.GetValueOrDefault(correlation), noStore, contentLength
            }));
            Assert.Equal(target, gateway.RawTarget); Assert.Equal(Session + "csrf", gateway.DecodedPath);
            Assert.Equal((int)status, gateway.Status);
            return new(status, noStore, contentLength, hasCookie, reached);
        }
        internal HttpClient Client(string peer, bool direct = false)
        {
            var address = new Uri(Address(direct ? Backend : Gateway)); var jar = new CookieContainer();
            var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, CookieContainer = jar,
                SslOptions = new() { RemoteCertificateValidationCallback = (_, presented, _, _) => presented?.GetCertHashString() == Certificate.Thumbprint },
                ConnectCallback = async (_, token) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try { socket.Bind(new IPEndPoint(IPAddress.Parse(peer), 0)); await socket.ConnectAsync(IPAddress.Loopback, address.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                } };
            var client = new HttpClient(new Tracking(this) { InnerHandler = handler }) { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(15) }; clients.Add(client); cookies[client] = jar; return client;
        }
        internal async Task<string> CsrfAsync(HttpClient? client = null)
        {
            using var response = await TrackAsync(await (client ?? Http).GetAsync(Session + "csrf"));
            var configured = Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes.Count(x => x.Key?.StartsWith("GatewayBrowserTransport:") == true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"csrf_status={(int)response.StatusCode} configured_routes={configured} pool_calls={Pool?.Calls ?? 0} backend_requests={Received.Count} backend_client={Received.LastOrDefault()?.Client}");
            await Success(response, HttpStatusCode.OK);
            if (response.Headers.Contains("Set-Cookie")) AssertCookie(response, "__Secure-ZekaCSRF");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return json.RootElement.GetProperty("csrfToken").GetString()!;
        }
        internal async Task<string> SignInAsync(string user)
        {
            using var response = await PostAsync("sign-in", await CsrfAsync(), body: JsonSerializer.Serialize(new { email = user + ".gateway@example.invalid", password = Password }));
            await Success(response, HttpStatusCode.OK); AssertCookie(response, BrowserSessionEndpoints.CookieName); return await Token(response);
        }
        internal async Task<HttpResponseMessage> GetAsync(string path, string? token = null)
        {
            Remember(token);
            using var request = new HttpRequestMessage(HttpMethod.Get, path); if (token is not null) request.Headers.Authorization = new("Bearer", token); return await TrackAsync(await Http.SendAsync(request));
        }
        internal async Task<HttpResponseMessage> PostAsync(string action, string csrf, string? origin = Origin, string? body = null,
            bool knownLength = true, string contentType = "application/json; charset=utf-8", HttpClient? client = null)
        {
            body ??= action == "sign-in" ? JsonSerializer.Serialize(new { email = "ada.gateway@example.invalid", password = Password }) : "{}";
            using var request = new HttpRequestMessage(HttpMethod.Post, Session + action);
            request.Content = knownLength ? new ByteArrayContent(Encoding.UTF8.GetBytes(body)) : new ChunkedJson(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (origin is not null) request.Headers.Add("Origin", origin); request.Headers.Add("X-Zeka-CSRF", csrf);
            Remember(csrf);
            return await TrackAsync(await (client ?? Http).SendAsync(request));
        }
        internal void AssertRedacted(string token)
        {
            Remember(token); Remember(Password); Remember(Alpha.ToString()); Remember(Beta.ToString());
            foreach (var user in Users) { Remember(user.Value.ToString()); Remember(user.Key + ".gateway@example.invalid"); }
            Assert.Contains(Logs.Events, e => e.Category == BrowserTransport.DiagnosticCategory && e.Event == 8901);
            GatewayLinuxValidation.RequireNoLeaks(Logs.Messages, historical);
            // Prove every retained generation is detectable without printing any value.
            foreach (var secret in historical)
                Assert.Throws<InvalidOperationException>(() => GatewayLinuxValidation.RequireNoLeaks(new[] { "synthetic-event " + secret }, historical));
            Assert.True(historical.Count >= 8, "historical privacy inventory is incomplete");
        }
        public ValueTask DisposeAsync() => DisposeOwnedAsync(false);
        internal async ValueTask DisposeOwnedAsync(bool suppressErrors)
        {
            Exception? first = null;
            foreach (var client in clients) try { client.Dispose(); } catch (Exception e) { first ??= e; }
            try { if (Gateway is not null) await Gateway.DisposeAsync(); } catch (Exception e) { first ??= e; }
            try { if (Legacy is not null) await Legacy.DisposeAsync(); } catch (Exception e) { first ??= e; }
            try { if (Backend is not null) await Backend.DisposeAsync(); } catch (Exception e) { first ??= e; }
            try { Auth?.Dispose(); } catch (Exception e) { first ??= e; }
            try { Certificate?.Dispose(); } catch (Exception e) { first ??= e; }
            try { if (KeyPath.Length != 0) File.Delete(KeyPath); } catch (Exception e) { first ??= e; }
            if (!suppressErrors && first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
        }
    }
    private sealed class ChunkedJson(string body) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(Encoding.UTF8.GetBytes(body)).AsTask();
    }
}

// Dedicated Linux child startup is the trust boundary. No store imports, handler
// certificate callbacks, shared-testhost trust mutations or inherited package secrets.
internal static class GatewayLinuxValidation
{
    internal const string Mode = "ZEKA_GATEWAY_CHILD_MODE";
    private const string Owner = "ZEKA_GATEWAY_CHILD_OWNER";
    private const string ChildTest = "Infrastructure.IntegrationTests.GatewayBrowserTransportTests.Isolated_gateway_case_and_real_handler_controls";
    internal sealed record CaseInput(string Method, JsonElement[] Arguments);
    private sealed record TransportInput(string Target, string Correlation, string Leaf, string Issuer, string Proxy, bool Pause);
    private sealed record Result(bool Accepted, bool CertificateSpecific, bool NameValid, bool TimeValid,
        bool UntrustedRoot, bool CookiesDisabled, bool RedirectDisabled, bool ProxyBypassed, string[] ExceptionKinds);

    internal static void Check(bool condition, string stage)
    { if (!condition) throw new InvalidOperationException("gateway-control-" + stage); }

    internal static void RequireNoLeaks(IEnumerable<string> events, IEnumerable<string> historical)
    {
        foreach (var text in events)
            foreach (var value in historical.Where(value => !string.IsNullOrEmpty(value)))
                if (text.Contains(value, StringComparison.Ordinal)) throw new InvalidOperationException("gateway-privacy-violation");
    }

    internal static CaseInput ReadCase() => JsonSerializer.Deserialize<CaseInput>(File.ReadAllText(Path.Combine(
        Environment.GetEnvironmentVariable(Owner)!, "case.json")))!;

    private static string DirectoryOwned()
    {
        Check(OperatingSystem.IsLinux(), "linux-required");
        var path = Path.Combine(Path.GetTempPath(), "zeka-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static X509Certificate2 Root(RSA key)
    {
        var request = new CertificateRequest("CN=Zeka-Local-Gateway-" + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.Date.AddDays(-1), DateTimeOffset.UtcNow.Date.AddDays(2));
    }

    private static X509Certificate2 Leaf(X509Certificate2 issuer, RSA key)
    {
        var request = new CertificateRequest("CN=browser.example.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("browser.example.invalid"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(issuer, DateTimeOffset.UtcNow.Date.AddHours(-1), DateTimeOffset.UtcNow.Date.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    internal static X509Certificate2 BrowserCertificate()
    {
        Check(Environment.GetEnvironmentVariable(Mode) == "browser", "browser-child-required");
        using var root = new X509Certificate2(File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable(Owner)!, "root.pfx")));
        using var key = RSA.Create(2048);
        using var certificate = Leaf(root, key);
        return new X509Certificate2(certificate.Export(X509ContentType.Pfx));
    }

    internal static async Task<bool> RunCaseAsync(string method, object?[] arguments, ITestOutputHelper output)
    {
        if (Environment.GetEnvironmentVariable(Mode) == "browser") return false;
        var directory = DirectoryOwned();
        var parentFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        var parentDirectory = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
        using var key = RSA.Create(2048); using var root = Root(key);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "root.pfx"), root.Export(X509ContentType.Pfx));
            File.SetUnixFileMode(Path.Combine(directory, "root.pfx"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem());
            Directory.CreateDirectory(Path.Combine(directory, "empty-ca"));
            File.WriteAllText(Path.Combine(directory, "case.json"), JsonSerializer.Serialize(new { Method = method, Arguments = arguments }));
            await ChildAsync(directory, "browser", true, false, method, output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
            var absent = Absent(directory);
            Receipt(output, new { phase = "browser-child-cleanup", method, directoryAbsent = absent,
                parentTrustUnchanged = parentFile == Environment.GetEnvironmentVariable("SSL_CERT_FILE") && parentDirectory == Environment.GetEnvironmentVariable("SSL_CERT_DIR") });
            Check(absent == true, "browser-owned-directory-absence");
        }
        return true;
    }

    private static string Evidence()
    {
        var path = Environment.GetEnvironmentVariable("ZEKA_GATEWAY_EVIDENCE") ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "gateway-local");
        Directory.CreateDirectory(path); return path;
    }
    private static void Receipt(ITestOutputHelper output, object value)
    {
        var line = JsonSerializer.Serialize(value);
        File.AppendAllText(Path.Combine(Evidence(), "gateway-receipts.jsonl"), line + Environment.NewLine);
        output.WriteLine(line);
    }

    private static async Task<Result?> ChildAsync(string directory, string mode, bool trust, bool kill, string identity, ITestOutputHelper output)
    {
        var run = Guid.NewGuid().ToString("N");
        var resultFile = Path.Combine(directory, "result-" + run + ".json");
        var native = Path.Combine(Evidence(), "child-" + run); Directory.CreateDirectory(native);
        var dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "vstest", typeof(GatewayBrowserTransportTests).Assembly.Location,
            "/TestCaseFilter:FullyQualifiedName=" + ChildTest, "/Logger:trx;LogFileName=child.trx", "/ResultsDirectory:" + native }) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "HOME", "TMPDIR", "DOTNET_ROOT", "NUGET_PACKAGES", "DOCKER_HOST", "TESTCONTAINERS_HOST_OVERRIDE" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment[Mode] = mode; start.Environment[Owner] = directory;
        start.Environment["ZEKA_GATEWAY_RESULT"] = resultFile;
        start.Environment["ZEKA_GATEWAY_EVIDENCE"] = native;
        if (mode == "transport")
        {
            var input = JsonSerializer.Deserialize<TransportInput>(File.ReadAllText(Path.Combine(directory, "transport.json")))!;
            foreach (var variable in new[] { "HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY" }) start.Environment[variable] = input.Proxy;
            start.Environment["NO_PROXY"] = "";
        }
        if (trust) { start.Environment["SSL_CERT_FILE"] = Path.Combine(directory, "ca.pem"); start.Environment["SSL_CERT_DIR"] = Path.Combine(directory, "empty-ca"); }
        File.WriteAllText(Path.Combine(native, "command.json"), JsonSerializer.Serialize(new { executable = dotnet, arguments = start.ArgumentList.ToArray(), identity, mode, trust, kill,
            source = Environment.GetEnvironmentVariable("ZEKA_GATEWAY_SHA"), expectedNativeTest = ChildTest }));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("gateway-child-start");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        var started = DateTimeOffset.UtcNow;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            if (kill)
            {
                while (!File.Exists(resultFile)) { Check(!process.HasExited, "abnormal-premature-exit"); await Task.Delay(50, deadline.Token); }
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr);
            var trxPath = Path.Combine(native, "child.trx");
            int? executed = null, passed = null;
            if (!kill)
            {
                Check(process.ExitCode == 0, "child-exit");
                var trx = System.Xml.Linq.XDocument.Load(trxPath);
                var counters = trx.Descendants().Single(x => x.Name.LocalName == "Counters");
                executed = int.Parse(counters.Attribute("executed")!.Value); passed = int.Parse(counters.Attribute("passed")!.Value);
                Check(executed == 1 && passed == 1 && counters.Attribute("failed")!.Value == "0" && counters.Attribute("notExecuted")!.Value == "0", "child-native-accounting");
            }
            else Check(process.ExitCode != 0, "abnormal-nonzero-exit");
            Receipt(output, new { phase = "child-process", identity, mode, started, finished = DateTimeOffset.UtcNow,
                exitCode = process.ExitCode, killed = kill, executed, passed, trxPresent = File.Exists(trxPath),
                stdoutBytes = Encoding.UTF8.GetByteCount(await stdout), stderrBytes = Encoding.UTF8.GetByteCount(await stderr),
                assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GatewayBrowserTransportTests).Assembly.Location))),
                credentialEnvironmentInherited = false });
            return mode == "transport" ? JsonSerializer.Deserialize<Result>(File.ReadAllText(resultFile)) : null;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(stdout, stderr);
            Receipt(output, new { phase = "child-final-absence", identity, exited = process.HasExited, exitCode = process.ExitCode,
                stdoutBytes = Encoding.UTF8.GetByteCount(await stdout), stderrBytes = Encoding.UTF8.GetByteCount(await stderr) });
        }
    }

    internal static async Task TransportChildAsync()
    {
        var directory = Environment.GetEnvironmentVariable(Owner)!;
        var input = JsonSerializer.Deserialize<TransportInput>(File.ReadAllText(Path.Combine(directory, "transport.json")))!;
        using var leaf = new X509Certificate2(File.ReadAllBytes(input.Leaf)); using var issuer = new X509Certificate2(File.ReadAllBytes(input.Issuer));
        using var chain = new X509Chain(); chain.ChainPolicy.ExtraStore.Add(issuer); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        _ = chain.Build(leaf); // Diagnostic only. Never assigned to the Ocelot handler.
        var nameValid = leaf.MatchesHostname("127.0.0.1", false, false);
        var timeValid = DateTime.UtcNow > leaf.NotBefore.ToUniversalTime() && DateTime.UtcNow < leaf.NotAfter.ToUniversalTime();
        var untrustedRoot = chain.ChainStatus.Any(x => x.Status.HasFlag(X509ChainStatusFlags.UntrustedRoot));
        var target = new Uri(input.Target); Check(target.Scheme == "https" && target.AbsolutePath == "/probe", "https-target");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] }); builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Routes:0:Key"] = "GatewayDefaultHandler", ["Routes:0:UpstreamPathTemplate"] = "/probe", ["Routes:0:UpstreamHttpMethod:0"] = "GET",
            ["Routes:0:DownstreamPathTemplate"] = "/probe", ["Routes:0:DownstreamScheme"] = "https",
            ["Routes:0:DownstreamHostAndPorts:0:Host"] = "127.0.0.1", ["Routes:0:DownstreamHostAndPorts:0:Port"] = target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:0:DangerousAcceptAnyServerCertificateValidator"] = "false", ["Routes:0:HttpHandlerOptions:AllowAutoRedirect"] = "false",
            ["Routes:0:HttpHandlerOptions:UseCookieContainer"] = "false", ["Routes:0:HttpHandlerOptions:UseProxy"] = "false"
        });
        builder.Services.AddOcelot(builder.Configuration); await using var app = builder.Build(); await app.UseOcelot();
        var pool = app.Services.GetRequiredService<IMessageInvokerPool>(); Check(pool.GetType() == typeof(MessageInvokerPool), "real-pool");
        var route = app.Services.GetRequiredService<IInternalConfigurationRepository>().Get().Data.Routes.Single().DownstreamRoute.Single();
        Check(!route.DangerousAcceptAnyServerCertificateValidator && route.DownstreamScheme == "https", "default-certificate-policy");
        using var invoker = pool.Get(route); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Result result;
        try
        {
            async Task<HttpResponseMessage> Send(string control)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, target); request.Headers.Add("X-Zeka-Control", control); request.Headers.Add("X-Zeka-Receipt", input.Correlation);
                return await invoker.SendAsync(request, timeout.Token);
            }
            using var first = await Send("cookie"); Check((int)first.StatusCode == 204 && first.Headers.GetValues("X-Zeka-Receipt").Single() == input.Correlation, "trusted-receipt");
            using var second = await Send("cookie-check"); var cookiesDisabled = second.Headers.GetValues("X-Zeka-Cookie").Single() == "absent";
            using var redirect = await Send("redirect"); var redirectDisabled = redirect.StatusCode == HttpStatusCode.Redirect;
            // Discriminating cookie/redirect controls retain normal default TLS validation.
            using var controlHandler = new SocketsHttpHandler { UseCookies = true, AllowAutoRedirect = true, UseProxy = false };
            using var controlClient = new HttpClient(controlHandler);
            using var cookieRequest = new HttpRequestMessage(HttpMethod.Get, target); cookieRequest.Headers.Add("X-Zeka-Control", "cookie");
            using var cookieResponse = await controlClient.SendAsync(cookieRequest, timeout.Token);
            using var checkRequest = new HttpRequestMessage(HttpMethod.Get, target); checkRequest.Headers.Add("X-Zeka-Control", "cookie-check");
            using var checkResponse = await controlClient.SendAsync(checkRequest, timeout.Token);
            Check(checkResponse.Headers.GetValues("X-Zeka-Cookie").Single() == "present", "cookie-positive-control");
            using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, target); redirectRequest.Headers.Add("X-Zeka-Control", "redirect");
            using var redirectResponse = await controlClient.SendAsync(redirectRequest, timeout.Token);
            Check(redirectResponse.Headers.GetValues("X-Zeka-Redirect").Single() == "followed", "redirect-positive-control");
            using var proxyHandler = new SocketsHttpHandler { UseProxy = true, Proxy = new ForcedProxy(new Uri(input.Proxy)) };
            using var proxyClient = new HttpClient(proxyHandler);
            var proxyRejected = false;
            try { using var ignored = await proxyClient.GetAsync(target, timeout.Token); }
            catch (HttpRequestException error) { proxyRejected = error.HttpRequestError == HttpRequestError.ProxyTunnelError; }
            Check(proxyRejected, "proxy-positive-control");
            result = new(true, false, nameValid, timeValid, untrustedRoot, cookiesDisabled, redirectDisabled, proxyRejected, []);
        }
        catch (HttpRequestException error)
        {
            var kinds = new List<string>(); var authentication = false;
            for (Exception? current = error; current is not null; current = current.InnerException)
            {
                authentication |= current is System.Security.Authentication.AuthenticationException;
                kinds.Add(current switch { HttpRequestException => "http", System.Security.Authentication.AuthenticationException => "authentication", IOException => "io", SocketException => "socket", _ => "other" });
            }
            result = new(false, error.HttpRequestError == HttpRequestError.SecureConnectionError && authentication,
                nameValid, timeValid, untrustedRoot, false, false, false, kinds.ToArray());
        }
        var path = Environment.GetEnvironmentVariable("ZEKA_GATEWAY_RESULT")!;
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(result)); File.Move(path + ".tmp", path);
        if (input.Pause) await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    internal static async Task ProveControlsAsync(ITestOutputHelper output)
    {
        Check(OperatingSystem.IsLinux(), "linux-required");
        var parentFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE"); var parentDirectory = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
        foreach (var lifecycle in new[] { "normal", "startup-failure", "abnormal-child" })
        {
            var directory = DirectoryOwned(); using var rootKey = RSA.Create(2048); using var otherKey = RSA.Create(2048); using var leafKey = RSA.Create(2048);
            using var root = Root(rootKey); using var otherRoot = Root(otherKey); using var trusted = Leaf(root, leafKey); using var untrusted = Leaf(otherRoot, leafKey);
            Check(trusted.NotBefore == untrusted.NotBefore && trusted.NotAfter == untrusted.NotAfter, "matched-validity");
            var received = new ConcurrentDictionary<string, int>(); WebApplication? good = null, bad = null, proxy = null;
            int? goodPort = null, badPort = null, proxyPort = null; var proxyHits = 0;
            var planned = false; Exception? failure = null; var cleanup = new Dictionary<string, bool?>();
            try
            {
                File.WriteAllBytes(Path.Combine(directory, "root.cer"), root.RawData); File.WriteAllBytes(Path.Combine(directory, "other.cer"), otherRoot.RawData);
                File.WriteAllBytes(Path.Combine(directory, "good.cer"), trusted.RawData); File.WriteAllBytes(Path.Combine(directory, "bad.cer"), untrusted.RawData);
                good = await EndpointAsync(trusted, received); bad = await EndpointAsync(untrusted, received);
                goodPort = new Uri(Address(good)).Port; badPort = new Uri(Address(bad)).Port;
                var proxyBuilder = WebApplication.CreateBuilder(); proxyBuilder.Logging.ClearProviders();
                proxyBuilder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
                proxy = proxyBuilder.Build(); proxy.Run(http => { Interlocked.Increment(ref proxyHits); http.Response.StatusCode = 502; return Task.CompletedTask; });
                await proxy.StartAsync(); proxyPort = new Uri(Address(proxy)).Port;
                await Control("before", good, false, false, false);
                File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem()); Directory.CreateDirectory(Path.Combine(directory, "empty-ca"));
                try
                {
                    if (lifecycle == "startup-failure") throw new PlannedStartupFailure();
                    await Control("trusted", good, true, true, false);
                    await Control("untrusted", bad, false, true, false);
                    if (lifecycle == "abnormal-child") await Control("terminated", good, true, true, true);
                }
                catch (PlannedStartupFailure) { planned = true; }
                File.Delete(Path.Combine(directory, "ca.pem")); Directory.Delete(Path.Combine(directory, "empty-ca"));
                await Control("after", good, false, false, false);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                async Task Clean(string name, Func<Task> action)
                { try { await action(); cleanup[name] = true; } catch { cleanup[name] = false; } }
                await Clean("trusted-dispose", async () => { if (good is not null) await good.DisposeAsync(); });
                await Clean("untrusted-dispose", async () => { if (bad is not null) await bad.DisposeAsync(); });
                await Clean("proxy-dispose", async () => { if (proxy is not null) await proxy.DisposeAsync(); });
                cleanup["trusted-listener-absent"] = await ClosedAsync(goodPort); cleanup["untrusted-listener-absent"] = await ClosedAsync(badPort);
                cleanup["proxy-listener-absent"] = await ClosedAsync(proxyPort);
                await Clean("owned-directory-remove", () => { Directory.Delete(directory, true); return Task.CompletedTask; });
                cleanup["owned-directory-absent"] = Absent(directory); cleanup["root-store-absent"] = RootAbsent(root);
                cleanup["ca-file-absent"] = Absent(Path.Combine(directory, "ca.pem"));
                cleanup["ca-directory-absent"] = Absent(Path.Combine(directory, "empty-ca"));
                cleanup["parent-trust-unchanged"] = parentFile == Environment.GetEnvironmentVariable("SSL_CERT_FILE") && parentDirectory == Environment.GetEnvironmentVariable("SSL_CERT_DIR");
                Receipt(output, new { phase = "lifecycle-cleanup", lifecycle, plannedStartupFailure = planned, cleanup,
                    failureKind = failure is null ? "none" : "control-failure", unknownOutcomes = cleanup.Count(x => x.Value is null) });
            }
            Check(failure is null, "lifecycle-primary"); Check(cleanup.Values.All(x => x == true), "lifecycle-cleanup");
            Check(planned == (lifecycle == "startup-failure"), "planned-startup-failure");

            async Task Control(string name, WebApplication endpoint, bool accepted, bool scoped, bool kill)
            {
                var correlation = Guid.NewGuid().ToString("N"); var target = new Uri(new Uri(Address(endpoint)), "/probe");
                File.WriteAllText(Path.Combine(directory, "transport.json"), JsonSerializer.Serialize(new TransportInput(target.AbsoluteUri, correlation,
                    Path.Combine(directory, accepted || name is "before" or "after" ? "good.cer" : "bad.cer"),
                    Path.Combine(directory, accepted || name is "before" or "after" ? "root.cer" : "other.cer"), Address(proxy!), kill)));
                var proxyBefore = proxyHits;
                var result = await ChildAsync(directory, "transport", scoped, kill, lifecycle + "-" + name, output);
                Check(result is not null && result.Accepted == accepted && result.NameValid && result.TimeValid, "TLS-outcome");
                if (accepted) Check(result!.CookiesDisabled && result.RedirectDisabled && result.ProxyBypassed, "handler-flags");
                else Check(result!.CertificateSpecific && result.UntrustedRoot, "certificate-specific-rejection");
                Check(accepted ? proxyHits == proxyBefore + 1 : proxyHits == proxyBefore, "proxy-receipt-accounting");
                var count = received.GetValueOrDefault(correlation); Check(accepted ? count > 0 : count == 0, "downstream-accounting");
                Receipt(output, new { phase = "TLS-control", lifecycle, name, accepted, downstreamHttpReceipts = count, result });
            }
        }
    }

    private static async Task<WebApplication> EndpointAsync(X509Certificate2 certificate, ConcurrentDictionary<string, int> receipts)
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build(); app.Run(http =>
        {
            var correlation = http.Request.Headers["X-Zeka-Receipt"].ToString(); if (correlation.Length != 0) receipts.AddOrUpdate(correlation, 1, (_, n) => n + 1);
            http.Response.Headers["X-Zeka-Receipt"] = correlation; http.Response.StatusCode = 204;
            if (http.Request.Path == "/redirected") http.Response.Headers["X-Zeka-Redirect"] = "followed";
            else if (http.Request.Headers["X-Zeka-Control"] == "redirect") { http.Response.StatusCode = 302; http.Response.Headers.Location = "/redirected"; }
            else if (http.Request.Headers["X-Zeka-Control"] == "cookie") http.Response.Headers.SetCookie = "ZekaHandlerControl=synthetic; Path=/; Secure; HttpOnly";
            else if (http.Request.Headers["X-Zeka-Control"] == "cookie-check") http.Response.Headers["X-Zeka-Cookie"] = http.Request.Headers.Cookie.Count == 0 ? "absent" : "present";
            return Task.CompletedTask;
        });
        try { await app.StartAsync(); return app; } catch { await app.DisposeAsync(); throw; }
    }
    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static bool? Absent(string path)
    {
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; } catch (DirectoryNotFoundException) { return true; } catch { return null; }
    }
    private static async Task<bool?> ClosedAsync(int? port)
    {
        if (port is null) return null;
        try { using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port.Value).WaitAsync(TimeSpan.FromSeconds(3)); return false; }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused) { return true; } catch { return null; }
    }
    private static bool? RootAbsent(X509Certificate2 root)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadOnly);
            var certificates = store.Certificates; try { return !certificates.Cast<X509Certificate2>().Any(certificate => certificate.RawData.AsSpan().SequenceEqual(root.RawData)); }
            finally { foreach (var certificate in certificates) certificate.Dispose(); }
        }
        catch { return null; }
    }
    private sealed class PlannedStartupFailure : Exception;
    private sealed class ForcedProxy(Uri endpoint) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => endpoint;
        public bool IsBypassed(Uri host) => false;
    }
}
