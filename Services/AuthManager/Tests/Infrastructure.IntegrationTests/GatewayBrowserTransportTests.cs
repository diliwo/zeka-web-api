using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        await using var host = await StartAsync();
        var token = await host.SignInAsync("ada");
        var paths = new[] { Session + "csrf", Session + "sign-in", Session + "restore", Session + "logout", Eligible, "/api/v1/tenant-access/" + host.Alpha };
        for (var index = 0; index < paths.Length; index++)
        {
            var right = index is 1 or 2 or 3 ? HttpMethod.Post : HttpMethod.Get;
            foreach (var wrong in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options, HttpMethod.Head }.Where(x => x != right))
            {
                using var request = new HttpRequestMessage(wrong, paths[index]);
                using var response = await host.Http.SendAsync(request);
                await Error(response, HttpStatusCode.NotFound);
            }
            foreach (var path in new[] { paths[index] + "/", paths[index] + "/extra", paths[index].ToUpperInvariant() })
            {
                using var response = await host.Http.GetAsync(path); await Error(response, HttpStatusCode.NotFound);
            }
        }
        foreach (var path in new[] { "/api/v1/browser-session", "/api/v1/browser-session/unknown", "/api/v1/tenant-access/not-a-guid",
            "/api/v1/tenant-access/", "/api/v1/tenant-access/" + Guid.Empty, "/api/v1/tenant-access/" + host.Alpha + "/memberships/" + Guid.NewGuid(), "/api/v1/internal", "/api/v1/browser-session/%63srf" })
        {
            using var response = await host.Http.GetAsync(path); await Error(response, HttpStatusCode.NotFound);
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
        using (var response = await host.Http.GetAsync("/clients/search")) Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(host.Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes, x => x.UpstreamPathTemplate == "/login");
        output.WriteLine("six_method_path_matrix=true origin_csrf_and_credential_negatives=true internal_routes_closed=true final_assertions=true");
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Exact_json_headers_cookies_and_framing_cross_real_ocelot(bool knownLength)
    {
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
        await using var host = await StartAsync(configure: values => values[key] = value);
        using var response = await host.Http.GetAsync(Session + "csrf"); await Error(response, HttpStatusCode.ServiceUnavailable);
        Assert.Empty(host.Received);
        Assert.DoesNotContain(host.Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes,
            route => route.Key?.StartsWith("GatewayBrowserTransport:") == true);
    }

    [Fact]
    public async Task Unavailable_database_overflow_and_downstream_emit_generic_no_store_without_cached_choices()
    {
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
        await using var host = await StartAsync(); var token = await host.SignInAsync("ada");
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
        Action<Dictionary<string, string?>>? configure = null)
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
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=browser.example.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("browser.example.invalid"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build());
            using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            owned.Certificate = new X509Certificate2(temporary.Export(X509ContentType.Pfx));
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
            var gateway = WebApplication.CreateBuilder(); gateway.Logging.ClearProviders(); gateway.Logging.AddProvider(owned.Logs);
            gateway.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(owned.Certificate)));
            gateway.Configuration.AddJsonFile(Path.Combine(root.FullName, "ApiGateways/Ocelot.ApiGateway/ocelot.json"), optional: false, reloadOnChange: false);
            var values = new Dictionary<string, string?> { ["GatewayBrowserTransport:Origin"] = Origin, ["GatewayBrowserTransport:DownstreamOrigin"] = Address(owned.Backend) };
            for (var peer = 2; peer <= 8; peer++) values["GatewayBrowserTransport:ClientPeers:" + (peer - 2)] = "127.0.0." + peer;
            configure?.Invoke(values); gateway.Configuration.AddInMemoryCollection(values); BrowserTransport.Add(gateway);
            // Test-only TLS pin at Ocelot's transport-pool seam. The real route/middleware/requester and TCP/TLS hosts remain in use.
            gateway.Services.AddSingleton<IMessageInvokerPool>(_ => owned.Pool = new PinnedPool(owned.Certificate));
            owned.Gateway = gateway.Build(); owned.Gateway.UseBrowserTransport(); await owned.Gateway.UseOcelot(); await owned.Gateway.StartAsync();
            owned.Http = owned.Client("127.0.0.2"); return owned;
        }
        catch { await owned.DisposeOwnedAsync(suppressErrors: true); throw; }
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private sealed record Received(string Path, string Body, string? ContentType, string Origin, string Csrf, string Cookie, string Authorization, string Client, long? Length, bool Chunked);
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
        public ILogger CreateLogger(string category) => new Capture(this);
        public void Dispose() { }
        private sealed class Capture(LogCapture owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => owner.Messages.Enqueue(format(state, error));
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal WebApplication Backend = null!, Gateway = null!;
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
        private readonly List<HttpClient> clients = new();
        private readonly Dictionary<HttpClient, CookieContainer> cookies = new();
        internal BrowserSessionEndpoints.BrowserSessionAdmission Admission => Backend.Services.GetRequiredService<BrowserSessionEndpoints.BrowserSessionAdmission>();
        internal string? RefreshCookie => cookies[Http].GetCookies(new Uri(Origin + Session))[BrowserSessionEndpoints.CookieName]?.Value;
        internal AuthDbContext Administrator() => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(AdminConnection).Options);
        internal HttpClient DirectClient() => Client("127.0.0.9", direct: true);
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
            var client = new HttpClient(handler) { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(15) }; clients.Add(client); cookies[client] = jar; return client;
        }
        internal async Task<string> CsrfAsync(HttpClient? client = null)
        {
            using var response = await (client ?? Http).GetAsync(Session + "csrf");
            var configured = Gateway.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<FileConfiguration>>().Value.Routes.Count(x => x.Key?.StartsWith("GatewayBrowserTransport:") == true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"csrf_status={(int)response.StatusCode} configured_routes={configured} pool_calls={Pool.Calls} backend_requests={Received.Count} backend_client={Received.LastOrDefault()?.Client}");
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
            using var request = new HttpRequestMessage(HttpMethod.Get, path); if (token is not null) request.Headers.Authorization = new("Bearer", token); return await Http.SendAsync(request);
        }
        internal async Task<HttpResponseMessage> PostAsync(string action, string csrf, string? origin = Origin, string? body = null,
            bool knownLength = true, string contentType = "application/json; charset=utf-8", HttpClient? client = null)
        {
            body ??= action == "sign-in" ? JsonSerializer.Serialize(new { email = "ada.gateway@example.invalid", password = Password }) : "{}";
            using var request = new HttpRequestMessage(HttpMethod.Post, Session + action);
            request.Content = knownLength ? new ByteArrayContent(Encoding.UTF8.GetBytes(body)) : new ChunkedJson(body);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            if (origin is not null) request.Headers.Add("Origin", origin); request.Headers.Add("X-Zeka-CSRF", csrf);
            return await (client ?? Http).SendAsync(request);
        }
        internal void AssertRedacted(string token)
        {
            foreach (var message in Logs.Messages)
            { Assert.DoesNotContain(Password, message); Assert.DoesNotContain(token, message); Assert.DoesNotContain("ada.gateway@example.invalid", message); Assert.DoesNotContain(Alpha.ToString(), message); if (RefreshCookie is { } cookie) Assert.DoesNotContain(cookie, message); }
        }
        public ValueTask DisposeAsync() => DisposeOwnedAsync(false);
        internal async ValueTask DisposeOwnedAsync(bool suppressErrors)
        {
            Exception? first = null;
            foreach (var client in clients) try { client.Dispose(); } catch (Exception e) { first ??= e; }
            try { if (Gateway is not null) await Gateway.DisposeAsync(); } catch (Exception e) { first ??= e; }
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
