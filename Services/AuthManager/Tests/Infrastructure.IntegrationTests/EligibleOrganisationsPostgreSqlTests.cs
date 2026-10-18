using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Zeka.Authentication.Tests;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "89")]
[Trait("Slice", "Discovery")]
public sealed class EligibleOrganisationsPostgreSqlTests(PostgreSqlFixture fixture)
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(100)] [InlineData(101)]
    public async Task Actual_runtime_resolver_filters_before_bound_and_returns_complete_or_generic_overflow(int eligible)
    {
        await using var test = await PrepareAsync();
        await using (var db = test.Administrator())
        {
            for (var i = 0; i < eligible; i++) AddChoice(db, test.Ada, $"Alpha {i:D3}");
            // More than a bound of other-subject rows, inactive organisations and inactive memberships.
            for (var i = 0; i < 105; i++)
            {
                AddChoice(db, test.Ben, "Ben Beta");
                AddChoice(db, test.Ada, "Inactive Gamma", organisationStatus: OrganisationStatus.Suspended);
                AddChoice(db, test.Ada, "Left Gamma", membershipStatus: MembershipStatus.Left);
            }
            AddChoice(db, test.Ada, "Owner without membership", member: false);
            await db.SaveChangesAsync();
        }
        await using (var runtime = new NpgsqlConnection(test.Runtime))
        {
            await runtime.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT current_user || '|' || count(*)::text FROM public.\"OrganisationMemberships\" WHERE \"UserId\"=@subject", runtime);
            query.Parameters.AddWithValue("subject", test.Ben);
            Assert.Equal("zeka_auth_runtime|105", (string)(await query.ExecuteScalarAsync())!);
        }
        using var response = await test.Host.GetAsync(test.Bearer);
        if (eligible == 101) await DiscoveryEndpointHost.AssertErrorAsync(response, HttpStatusCode.Conflict);
        else
        {
            var choices = await ReadChoicesAsync(response);
            Assert.Equal(eligible, choices.Length);
            Assert.All(choices, choice => Assert.StartsWith("Alpha", choice.DisplayName));
        }
        Assert.Equal(101, test.Probe.Limit);
        Assert.Equal(test.Ada, test.Probe.Subject);
        Assert.Equal(IsolationLevel.RepeatableRead, test.Probe.Isolation);
        Assert.True(test.Probe.SubjectPredicate);
        // Exact catalog verifier plus a real login identity, without masked administrator access.
        await new RuntimeDatabaseIdentityValidator(test.Runtime, "zeka_auth_runtime").StartAsync(default);
        await using var catalog = test.Administrator();
        await RlsSecurityManifestVerifier.VerifyAsync(catalog, typeof(AuthDbContext).Assembly);
    }

    [Theory]
    [InlineData("en-US")] [InlineData("tr-TR")]
    public async Task Ordering_is_ordinal_with_canonical_guid_ties_and_preserved_names(string culture)
    {
        await using var test = await PrepareAsync();
        var names = new[] { "Zulu", "alpha", "Alpha", "İota", "Alpha" };
        var ids = new[] { "ffffffff-0000-0000-0000-000000000001", "00000001-0000-0000-0000-000000000001",
            "ffffffff-0000-0000-0000-000000000002", "10000000-0000-0000-0000-000000000001", "00000002-0000-0000-0000-000000000001" }.Select(Guid.Parse).ToArray();
        await using (var db = test.Administrator())
        {
            for (var i = 0; i < names.Length; i++) AddChoice(db, test.Ada, names[i], id: ids[i]);
            await db.SaveChangesAsync();
        }
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            using var scope = test.Host.Services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<IEligibleOrganisations>().ResolveAsync(test.Ada);
            Assert.Equal(new[] { ids[4], ids[2], ids[0], ids[1], ids[3] }, result.Organisations.Select(choice => choice.OrganisationId));
            Assert.Equal(new[] { "Alpha", "Alpha", "Zulu", "alpha", "İota" }, result.Organisations.Select(choice => choice.DisplayName));
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(OrganisationStatus.Pending)] [InlineData(OrganisationStatus.Suspended)]
    [InlineData(OrganisationStatus.Closing)] [InlineData(OrganisationStatus.Closed)]
    [InlineData(OrganisationStatus.Archived)]
    public async Task Inactive_organisation_never_becomes_a_choice(OrganisationStatus status)
    {
        await using var test = await PrepareAsync();
        await using (var db = test.Administrator()) { AddChoice(db, test.Ada, "Gamma", organisationStatus: status); await db.SaveChangesAsync(); }
        using var response = await test.Host.GetAsync(test.Bearer); Assert.Empty(await ReadChoicesAsync(response));
    }

    [Theory]
    [InlineData(MembershipStatus.Suspended)] [InlineData(MembershipStatus.Left)]
    public async Task Inactive_membership_is_excluded(MembershipStatus status)
    {
        await using var test = await PrepareAsync();
        await using (var db = test.Administrator()) { AddChoice(db, test.Ada, "Gamma", membershipStatus: status); await db.SaveChangesAsync(); }
        using var response = await test.Host.GetAsync(test.Bearer); Assert.Empty(await ReadChoicesAsync(response));
    }

    [Theory]
    [InlineData("suspended")] [InlineData("pending")] [InlineData("confirmation")]
    [InlineData("lockout")] [InlineData("missing")]
    public async Task Current_user_change_after_successful_session_check_is_401_not_empty_success(string change)
    {
        await using var test = await PrepareAsync();
        test.AfterSession = async () =>
        {
            await using var db = test.Administrator(); var user = await db.Users.SingleAsync(x => x.Id == test.Ada);
            if (change == "confirmation") user.EmailConfirmed = false;
            else if (change == "lockout") user.LockoutEnd = test.Host.Auth.Clock.GetUtcNow().AddHours(1);
            else db.Entry(user).Property(x => x.Status).CurrentValue = change == "suspended" ? UserStatus.Suspended : UserStatus.PendingVerification;
            await db.SaveChangesAsync();
        };
        if (change == "missing")
        {
            // Direct resolver absence is distinct from an active subject with no memberships.
            using var scope = test.Host.Services.CreateScope();
            Assert.Equal(EligibleOrganisationsOutcome.Unauthenticated,
                (await scope.ServiceProvider.GetRequiredService<IEligibleOrganisations>().ResolveAsync(Guid.NewGuid())).Outcome);
            return;
        }
        await test.Host.AssertAsync(HttpStatusCode.Unauthorized, test.Bearer);
        Assert.Equal(1, test.SuccessfulSessionChecks);
    }

    [Theory]
    [InlineData("membership")] [InlineData("organisation")] [InlineData("session")]
    public async Task Discovery_never_grants_access_or_resets_idle_and_live_selection_rechecks(string change)
    {
        await using var test = await PrepareAsync(); Guid organisation;
        await using (var db = test.Administrator()) { organisation = AddChoice(db, test.Ada, "Alpha"); await db.SaveChangesAsync(); }
        DateTimeOffset idle;
        await using (var db = test.Administrator()) idle = (await db.BrowserSessionFamilies.SingleAsync()).IdleExpiresAtUtc;
        test.Host.Auth.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var response = await test.Host.GetAsync(test.Bearer)) Assert.Single(await ReadChoicesAsync(response));
        await using (var db = test.Administrator())
        {
            Assert.Equal(idle, (await db.BrowserSessionFamilies.SingleAsync()).IdleExpiresAtUtc);
            Assert.Equal(1, await db.BrowserRefreshCredentials.CountAsync());
        }
        // Surface the native cause of an unchanged live-selection failure before its HTTP translation.
        using (var selected = test.Host.Services.CreateScope())
            Assert.Equal(TenantAccessOutcome.Authorized, (await selected.ServiceProvider
                .GetRequiredService<ICurrentTenantAccess>().ResolveAsync(test.Ada, organisation)).Outcome);
        using var before = await test.TenantAsync(organisation); Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        if (change == "session")
        {
            using var scope = test.Host.Services.CreateScope();
            Assert.Equal(BrowserSessionOutcome.Success, await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().LogoutAsync(test.Cookie, default));
        }
        else
        {
            await using var db = test.Administrator();
            if (change == "membership") (await db.OrganisationMemberships.SingleAsync()).Suspend(test.Host.Auth.Clock.GetUtcNow());
            else (await db.Organisations.SingleAsync()).Suspend(test.Host.Auth.Clock.GetUtcNow());
            await db.SaveChangesAsync();
        }
        using var denied = await test.TenantAsync(organisation);
        Assert.Equal(change == "session" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, denied.StatusCode);
        using var after = await test.Host.GetAsync(test.Bearer);
        if (change == "session") await DiscoveryEndpointHost.AssertErrorAsync(after, HttpStatusCode.Unauthorized);
        else Assert.Empty(await ReadChoicesAsync(after));
    }

    [Theory]
    [InlineData("revoked")] [InlineData("idle")] [InlineData("absolute")] [InlineData("mismatch")]
    [InlineData("unknown")]
    public async Task Actual_session_state_and_subject_binding_deny_discovery(string change)
    {
        await using var test = await PrepareAsync();
        await using (var db = test.Administrator())
        {
            var family = await db.BrowserSessionFamilies.SingleAsync();
            var now = test.Host.Auth.Clock.GetUtcNow();
            if (change == "revoked") family.RevokedAtUtc = now;
            if (change == "idle") family.IdleExpiresAtUtc = now;
            if (change == "absolute") family.AbsoluteExpiresAtUtc = now;
            await db.SaveChangesAsync();
        }
        var token = change is "mismatch" or "unknown"
            ? test.Host.Token([new("sub", (change == "mismatch" ? test.Ben : test.Ada).ToString()),
                new("sid", (change == "unknown" ? Guid.NewGuid() : test.SessionId).ToString())]) : test.Bearer;
        await test.Host.AssertAsync(HttpStatusCode.Unauthorized, token);
    }

    private static async Task<EligibleOrganisation[]> ReadChoicesAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("organisations").EnumerateArray().Select(x =>
            new EligibleOrganisation(x.GetProperty("organisationId").GetGuid(), x.GetProperty("displayName").GetString()!)).ToArray();
    }
    private static Guid AddChoice(AuthDbContext db, Guid subject, string name, OrganisationStatus organisationStatus = OrganisationStatus.Active,
        MembershipStatus membershipStatus = MembershipStatus.Active, bool member = true, Guid? id = null)
    {
        var now = DateTimeOffset.UtcNow; var organisation = Organisation.Create(id ?? Guid.NewGuid(), name, subject, now);
        db.Add(organisation); db.Entry(organisation).Property(x => x.Status).CurrentValue = organisationStatus;
        if (member)
        {
            var membership = OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, subject, now);
            db.Add(membership); db.Entry(membership).Property(x => x.Status).CurrentValue = membershipStatus;
        }
        return organisation.Id;
    }
    private async Task<Fixture> PrepareAsync()
    {
        var administrator = await fixture.CreateDatabaseAsync();
        await BootstrapAsync(administrator);
        await using (var db = Context(administrator))
        {
            await db.Database.OpenConnectionAsync(); await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await db.Database.MigrateAsync();
        }
        await BootstrapAsync(administrator);
        await using (var sql = new NpgsqlConnection(administrator))
        {
            await sql.OpenAsync(); await using var command = new NpgsqlCommand("ALTER ROLE zeka_auth_runtime PASSWORD 'fictional-discovery-runtime'", sql);
            await command.ExecuteNonQueryAsync();
        }
        var runtime = new NpgsqlConnectionStringBuilder(administrator)
            { Username = "zeka_auth_runtime", Password = "fictional-discovery-runtime", Pooling = false }.ConnectionString;
        var keyPath = Path.Combine(Path.GetTempPath(), $"fictional-discovery-{Guid.NewGuid():N}.key");
        await File.WriteAllBytesAsync(keyPath, RandomNumberGenerator.GetBytes(32));
        var probe = new ChoiceProbe(); Fixture? test = null;
        var host = await DiscoveryEndpointHost.StartAsync((builder, auth) =>
        {
            builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(runtime, provider => provider.EnableRetryOnFailure()).AddInterceptors(probe));
            builder.ConfigureMicrosoftIdentity(); builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
            var issuer = IssuerContractTests.Create(auth);
            issuer.ConfirmPublication(issuer.PrepareAsync().GetAwaiter().GetResult());
            builder.Services.AddSingleton<ISessionAccessTokenIssuer>(issuer);
            builder.Services.AddSingleton(new BrowserSessionPolicy { HashKeyReference = keyPath, AccessLifetime = TimeSpan.FromMinutes(5),
                IdleLifetime = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromDays(7) });
            builder.Services.AddScoped<IBrowserSessions>(services => new CheckedSession(
                ActivatorUtilities.CreateInstance<BrowserSessions>(services), async () =>
                { test!.SuccessfulSessionChecks++; if (test.AfterSession is { } action) { test.AfterSession = null; await action(); } }));
            builder.Services.AddScoped<IEligibleOrganisations, EligibleOrganisationsResolver>();
            builder.Services.AddScoped<ICurrentTenantAccess, CurrentTenantAccessResolver>();
            builder.Services.AddScoped<IActiveOrganisationMembership, ActiveOrganisationMembership>();
        });
        test = new Fixture(host, administrator, runtime, keyPath, probe);
        using (var scope = host.Services.CreateScope())
        {
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            foreach (var name in new[] { "ada", "ben" })
            {
                var user = User.Create(name + ".discovery@example.invalid", name + ".discovery", name, "Fictional", host.Auth.Clock.GetUtcNow());
                user.EmailConfirmed = true; Assert.True((await manager.CreateAsync(user, "Synthetic-Only!89")).Succeeded);
                await using var db = test.Administrator(); var stored = await db.Users.SingleAsync(x => x.Id == user.Id);
                db.Entry(stored).Property(x => x.Status).CurrentValue = UserStatus.Active; await db.SaveChangesAsync();
                if (name == "ada") test.Ada = user.Id; else test.Ben = user.Id;
            }
            var signed = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
                .SignInAsync("ada.discovery@example.invalid", "Synthetic-Only!89", null, default);
            Assert.Equal(BrowserSessionOutcome.Success, signed.Outcome);
            test.Bearer = signed.AccessToken!; test.Cookie = signed.RefreshSecret!;
            test.SessionId = Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(test.Bearer).GetClaim("sid").Value);
        }
        return test;
    }
    private static AuthDbContext Context(string connection) => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    private static async Task BootstrapAsync(string connection)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deployments/database/bootstrap-auth-roles.sql"))) directory = directory.Parent;
        await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "Deployments/database/bootstrap-auth-roles.sql")), sql);
        await command.ExecuteNonQueryAsync();
    }
    private sealed class Fixture(DiscoveryEndpointHost host, string administrator, string runtime, string keyPath, ChoiceProbe probe) : IAsyncDisposable
    {
        internal DiscoveryEndpointHost Host => host;
        internal string Runtime => runtime;
        internal ChoiceProbe Probe => probe;
        internal Guid Ada { get; set; }
        internal Guid Ben { get; set; }
        internal Guid SessionId { get; set; }
        internal string Bearer { get; set; } = "";
        internal string Cookie { get; set; } = "";
        internal Func<Task>? AfterSession { get; set; }
        internal int SuccessfulSessionChecks { get; set; }
        internal AuthDbContext Administrator() => Context(administrator);
        internal Task<HttpResponseMessage> TenantAsync(Guid organisation)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenant-access/{organisation:D}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer); return Host.Http.SendAsync(request);
        }
        public async ValueTask DisposeAsync() { await host.DisposeAsync(); File.Delete(keyPath); }
    }
    private sealed class ChoiceProbe : DbCommandInterceptor
    {
        internal int Limit { get; private set; }
        internal Guid Subject { get; private set; }
        internal IsolationLevel Isolation { get; private set; }
        internal bool SubjectPredicate { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("LIMIT") && command.CommandText.Contains("OrganisationMemberships"))
            {
                Limit = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<int>().Single();
                Subject = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid>().Single();
                Isolation = command.Transaction!.IsolationLevel;
                SubjectPredicate = command.CommandText.Contains("WHERE") && command.CommandText.Contains("\"UserId\" =");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class CheckedSession(IBrowserSessions inner, Func<Task> after) : IBrowserSessions
    {
        public async Task<BrowserSessionOutcome> CheckActiveAsync(Guid subject, Guid sid, CancellationToken token)
        { var result = await inner.CheckActiveAsync(subject, sid, token); if (result == BrowserSessionOutcome.Success) await after(); return result; }
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? cookie, CancellationToken token) => inner.SignInAsync(email, password, cookie, token);
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken token) => inner.RestoreAsync(cookie, token);
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken token) => inner.LogoutAsync(cookie, token);
    }
}
