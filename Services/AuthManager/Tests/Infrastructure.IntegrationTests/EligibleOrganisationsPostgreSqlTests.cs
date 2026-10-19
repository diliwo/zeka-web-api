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
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "89")]
[Trait("Slice", "Discovery")]
public sealed class EligibleOrganisationsPostgreSqlTests(PostgreSqlFixture fixture, ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Discovery_fixture_releases_only_its_connections_after_success_or_setup_failure(bool failSetup)
    {
        Fixture? owned = null;
        try
        {
            if (failSetup)
                await Assert.ThrowsAsync<InvalidOperationException>(() => PrepareAsync(test =>
                {
                    owned = test;
                    throw new InvalidOperationException("Synthetic setup failure after host acquisition");
                }));
            else
            {
                owned = await PrepareAsync();
                output.WriteLine("before-disposal owned_connections=" + await OwnedConnectionsAsync(owned));
                await owned.DisposeAsync();
            }
            Assert.NotNull(owned);
            var remaining = await OwnedConnectionsAsync(owned);
            output.WriteLine($"after-disposal setup_failure={failSetup} owned_connections={remaining} key_exists={File.Exists(owned.KeyPath)}");
            Assert.Equal(0, remaining);
            Assert.False(File.Exists(owned.KeyPath));
        }
        finally { if (owned is not null) await owned.DisposeAsync(); }
    }

    private static async Task<int> OwnedConnectionsAsync(Fixture test)
    {
        var settings = new NpgsqlConnectionStringBuilder(test.AdministratorConnection) { Pooling = false };
        await using var monitor = new NpgsqlConnection(settings.ConnectionString); await monitor.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*)::int FROM pg_stat_activity WHERE datname=@database AND pid<>pg_backend_pid()", monitor);
        command.Parameters.AddWithValue("database", settings.Database!);
        return (int)(await command.ExecuteScalarAsync())!;
    }

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
        var names = new[] { "Zulu", "alpha", "Alpha", "Ä°ota", "Alpha" };
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
            Assert.Equal(new[] { "Alpha", "Alpha", "Zulu", "alpha", "Ä°ota" }, result.Organisations.Select(choice => choice.DisplayName));
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
    [InlineData(false)] [InlineData(true)]
    public async Task Selected_access_retries_the_complete_transaction_and_honours_the_same_cancellation(bool cancel)
    {
        await using var test = await PrepareAsync(); Guid organisation;
        await using (var db = test.Administrator()) { organisation = AddChoice(db, test.Ada, "Alpha"); await db.SaveChangesAsync(); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        test.Probe.FailGrantOnce = true;
        test.Probe.CancelOnFailure = cancel ? deadline : null;
        using var scope = test.Host.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ICurrentTenantAccess>();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveAsync(test.Ada, organisation, deadline.Token));
        else
            Assert.Equal(TenantAccessOutcome.Authorized, (await resolver.ResolveAsync(test.Ada, organisation, deadline.Token)).Outcome);
        Assert.Equal(cancel ? 1 : 2, test.Probe.SelectionTransactions.Count);
        Assert.Equal(cancel ? 1 : 2, test.Probe.GrantAttempts);
        Assert.All(test.Probe.SelectionTransactions, transaction => Assert.Equal(IsolationLevel.RepeatableRead, transaction.Isolation));
        Assert.All(test.Probe.SelectionTenants, tenant => Assert.Equal(organisation.ToString("D"), tenant));
        if (!cancel) Assert.NotSame(test.Probe.SelectionTransactions[0].Transaction, test.Probe.SelectionTransactions[1].Transaction);
        // A selected-access query must leave the discovery-only probe untouched.
        Assert.Equal(0, test.Probe.Limit);
        // Both the failed and completed attempts must dispose their transaction.
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Null(dbContext.Database.CurrentTransaction);
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
            if (change is "idle" or "absolute")
            {
                // Persist a valid future deadline, then cross it with the controlled clock.
                family.AbsoluteExpiresAtUtc = now.AddMinutes(2);
                family.IdleExpiresAtUtc = change == "idle" ? now.AddMinutes(1) : family.AbsoluteExpiresAtUtc;
            }
            await db.SaveChangesAsync();
        }
        if (change is "idle" or "absolute")
            test.Host.Auth.Clock.Advance(TimeSpan.FromMinutes(change == "idle" ? 1 : 2));
        var token = change is "mismatch" or "unknown" or "idle" or "absolute"
            ? test.Host.Token([new("sub", (change == "mismatch" ? test.Ben : test.Ada).ToString()),
                new("sid", (change == "unknown" ? Guid.NewGuid() : test.SessionId).ToString())]) : test.Bearer;
        await test.Host.AssertAsync(HttpStatusCode.Unauthorized, token);
        Assert.Equal(1, test.SessionChecks);
        Assert.Equal(0, test.SuccessfulSessionChecks);
        Assert.True(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).ValidTo > test.Host.Auth.Clock.GetUtcNow().UtcDateTime);
        Assert.Equal(0, test.Probe.Limit);
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
    private async Task<Fixture> PrepareAsync(Action<Fixture>? setupProbe = null)
    {
        // Each discovery case owns a distinct database. Do not retain an idle global pool per case.
        var administrator = new NpgsqlConnectionStringBuilder(await fixture.CreateDatabaseAsync())
            { Pooling = false }.ConnectionString;
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
        var probe = new ChoiceProbe(); Fixture? test = null; DiscoveryEndpointHost? host = null;
        try
        {
            host = await DiscoveryEndpointHost.StartAsync((builder, auth) =>
            {
                builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(runtime, provider => provider.EnableRetryOnFailure()).AddInterceptors(probe));
                builder.ConfigureMicrosoftIdentity(); builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
                var issuer = IssuerContractTests.Create(auth);
                issuer.ConfirmPublication(issuer.PrepareAsync().GetAwaiter().GetResult());
                builder.Services.AddSingleton<ISessionAccessTokenIssuer>(issuer);
                builder.Services.AddSingleton(new BrowserSessionPolicy { HashKeyReference = keyPath, AccessLifetime = TimeSpan.FromMinutes(5),
                    IdleLifetime = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromDays(7) });
                builder.Services.AddScoped<IBrowserSessions>(services => new CheckedSession(
                    ActivatorUtilities.CreateInstance<BrowserSessions>(services), () => test!.SessionChecks++, async () =>
                    { test!.SuccessfulSessionChecks++; if (test.AfterSession is { } action) { test.AfterSession = null; await action(); } }));
                builder.Services.AddScoped<IEligibleOrganisations, EligibleOrganisationsResolver>();
                builder.Services.AddScoped<ICurrentTenantAccess, CurrentTenantAccessResolver>();
                builder.Services.AddScoped<IActiveOrganisationMembership, ActiveOrganisationMembership>();
            });
            test = new Fixture(host, administrator, runtime, keyPath, probe);
            setupProbe?.Invoke(test);
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
        catch
        {
            if (test is not null) await test.DisposeAsync();
            else
            {
                if (host is not null) await host.DisposeAsync();
                File.Delete(keyPath);
            }
            throw;
        }
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
        internal string AdministratorConnection => administrator;
        internal string KeyPath => keyPath;
        internal ChoiceProbe Probe => probe;
        internal Guid Ada { get; set; }
        internal Guid Ben { get; set; }
        internal Guid SessionId { get; set; }
        internal string Bearer { get; set; } = "";
        internal string Cookie { get; set; } = "";
        internal Func<Task>? AfterSession { get; set; }
        internal int SuccessfulSessionChecks { get; set; }
        internal int SessionChecks { get; set; }
        internal AuthDbContext Administrator() => Context(administrator);
        internal Task<HttpResponseMessage> TenantAsync(Guid organisation)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/tenant-access/{organisation:D}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Bearer); return Host.Http.SendAsync(request);
        }
        private bool disposed;
        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            try { await host.DisposeAsync(); }
            finally { File.Delete(keyPath); }
        }
    }
    private sealed class ChoiceProbe : DbCommandInterceptor
    {
        internal bool FailGrantOnce { get; set; }
        internal CancellationTokenSource? CancelOnFailure { get; set; }
        internal int GrantAttempts { get; private set; }
        internal List<(DbTransaction Transaction, IsolationLevel Isolation)> SelectionTransactions { get; } = [];
        internal List<string> SelectionTenants { get; } = [];
        internal int Limit { get; private set; }
        internal Guid Subject { get; private set; }
        internal IsolationLevel Isolation { get; private set; }
        internal bool SubjectPredicate { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"PermissionSets\"") && command.CommandText.Contains("\"OrganisationMemberships\""))
            {
                SelectionTransactions.Add((command.Transaction!, command.Transaction!.IsolationLevel));
                await using var tenant = command.Connection!.CreateCommand();
                tenant.Transaction = command.Transaction;
                tenant.CommandText = "SELECT current_setting('zeka.organisation_id', true)";
                SelectionTenants.Add((string)(await tenant.ExecuteScalarAsync(cancellationToken))!);
            }
            if (command.CommandText.Contains("\"MembershipPermissionGrants\""))
            {
                GrantAttempts++;
                if (FailGrantOnce)
                {
                    FailGrantOnce = false;
                    CancelOnFailure?.Cancel();
                    throw new NpgsqlException("Synthetic read interruption after membership observation", new TimeoutException());
                }
            }
            // Tenant-access also reads memberships with LIMIT, but has a selected-organisation predicate.
            if (command.CommandText.Contains("LIMIT") && command.CommandText.Contains("OrganisationMemberships")
                && !command.CommandText.Contains("\"PermissionSets\""))
            {
                Limit = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<int>().Single();
                Subject = command.Parameters.Cast<DbParameter>().Select(p => p.Value).OfType<Guid>().Single();
                Isolation = command.Transaction!.IsolationLevel;
                SubjectPredicate = command.CommandText.Contains("WHERE") && command.CommandText.Contains("\"UserId\" =");
            }
            return result;
        }
    }
    private sealed class CheckedSession(IBrowserSessions inner, Action before, Func<Task> after) : IBrowserSessions
    {
        public async Task<BrowserSessionOutcome> CheckActiveAsync(Guid subject, Guid sid, CancellationToken token)
        { before(); var result = await inner.CheckActiveAsync(subject, sid, token); if (result == BrowserSessionOutcome.Success) await after(); return result; }
        public Task<BrowserSessionResult> SignInAsync(string email, string password, string? cookie, CancellationToken token) => inner.SignInAsync(email, password, cookie, token);
        public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken token) => inner.RestoreAsync(cookie, token);
        public Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken token) => inner.LogoutAsync(cookie, token);
    }
}
