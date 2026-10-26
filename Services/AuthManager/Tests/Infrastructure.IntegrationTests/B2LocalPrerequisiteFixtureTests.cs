using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.TimeZones;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Zeka.LocalB2Prerequisites;

/// <summary>
/// Explicitly selected, persistent-target TEST setup. No class/collection fixture, constructor,
/// Testcontainers, migration, fabricated bearer, or ordinary-suite database side effect.
/// Each live fact requires its exact operation name and the approved run manifest.
/// </summary>
public sealed class B2LocalPrerequisiteFixtureTests
{
    private const string SourceCommit = "198a66015a5a90e2936f3caa263774d74011d1ec";
    private static readonly string[] UserNames =
        ["ownerA", "ownerB", "editor", "viewer", "eligibleTarget", "ineligibleTarget", "crossTenantTarget", "operator"];

    [Fact]
    public void Ordinary_suite_is_inert_without_both_explicit_inputs()
    {
        Assert.False(IsSelected(null, null, "Initialize"));
        Assert.False(IsSelected("Initialize", null, "Initialize"));
        Assert.False(IsSelected(null, "manifest.json", "Initialize"));
        Assert.False(IsSelected("Readback", "manifest.json", "Initialize"));
        Assert.True(IsSelected("Initialize", "manifest.json", "Initialize"));
    }

    [Theory]
    [InlineData("Production", true, SourceCommit)]
    [InlineData("Development", false, SourceCommit)]
    [InlineData("Development", true, "0000000000000000000000000000000000000000")]
    public void Manifest_rejects_environment_ownership_or_source_mismatch(string environment, bool exclusive, string source)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateIdentity(environment, exclusive, source));
    }

    [Theory]
    [InlineData("Host=remote.invalid;Port=5432;Database=zekaAuthdb;Username=zeka_auth_runtime")]
    [InlineData("Host=127.0.0.1;Port=5433;Database=zekaAuthdb;Username=zeka_auth_runtime")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=other;Username=zeka_auth_runtime")]
    [InlineData("Host=127.0.0.1;Port=5432;Database=zekaAuthdb;Username=postgres")]
    public void Runtime_connection_rejects_target_or_role_mismatch_without_opening_it(string connection)
    {
        Assert.Throws<InvalidOperationException>(() => ValidateConnection(connection, "zeka_auth_runtime"));
    }

    [Fact]
    public void Public_fixture_projection_contains_identifiers_and_no_credentials()
    {
        var fixture = NewFixture();
        var text = fixture.ToJsonString();
        Assert.DoesNotContain("password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fixture["newOrganisationIds"]!.AsArray().Count);
        Assert.NotEqual(Id(fixture, "tenantA"), Id(fixture, "tenantB"));
    }

    [Fact] public Task VerifyCatalog() => Run("VerifyCatalog", async (run, token) =>
    {
        await using var database = await run.Runtime(token);
        var actual = await database.PermissionSets.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Code, token);
        foreach (var expected in PermissionSet.SystemPermissionSets)
            Require(actual.TryGetValue(expected.Id, out var code) && code == expected.Code, "System permission catalogue mismatch.");
        Require(TenantPermissions.Resolve("Editor")!.Contains("Clients.EditAll")
            && TenantPermissions.Resolve("Viewer")!.Contains("Clients.ViewAll")
            && !TenantPermissions.Resolve("Viewer")!.Contains("Clients.EditAll")
            && TenantPermissions.Resolve("Admin")!.Contains("TeamConfiguration.ManageStaffProfiles"), "Permission contract mismatch.");
        await run.Receipt("catalogVerified", token);
    });

    [Fact] public Task Initialize() => Run("Initialize", async (run, token) =>
    {
        var existing = run.Manifest["fixtures"] as JsonObject;
        Require(existing is null || existing.Count == 0, "Fixture initialization already planned; reconcile instead of retrying.");
        var fixture = NewFixture();
        await run.UpdateManifest(doc => { doc["fixtures"] = fixture.DeepClone(); doc["fixtureTimeZone"]!["newOrganisationIds"] = fixture["newOrganisationIds"]!.DeepClone(); }, token);
        var credentials = new JsonObject { ["schemaVersion"] = 1, ["runId"] = run.RunId };
        var users = new JsonObject();
        credentials["users"] = users;
        await using var database = await run.Runtime(token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(database);
        services.AddIdentityCore<User>(options =>
        {
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
        }).AddEntityFrameworkStores<AuthDbContext>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var now = DateTimeOffset.UtcNow;
        foreach (var name in UserNames)
        {
            var email = $"b2-{run.RunId}-{name}@example.invalid";
            Require(email.Length <= 200, "Run identifier is too long for synthetic identity.");
            var user = User.Create(email, email, "Synthetic", name, now);
            user.Id = Id(fixture[name]!, "userId");
            user.EmailConfirmed = true;
            var password = "Aa1!" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var created = await manager.CreateAsync(user, password);
            Require(created.Succeeded, "Synthetic Identity creation failed.");
            database.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Active;
            users[name] = new JsonObject { ["userId"] = user.Id.ToString("D"), ["email"] = email, ["password"] = password };
        }
        foreach (var suffix in new[] { "A", "B" })
        {
            var owner = fixture["owner" + suffix]!;
            var organisation = Organisation.Create(Id(fixture, "tenant" + suffix),
                $"Synthetic B2 {run.RunId} {suffix}", Id(owner, "userId"), now);
            Require(organisation.Activate(now), "Synthetic organisation activation failed.");
            database.Organisations.Add(organisation);
            database.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(
                Id(owner, "membershipId"), organisation.Id, Id(owner, "userId"), now));
        }
        AddMember(database, fixture, "editor", "tenantA", PermissionSet.EditorId, now);
        AddMember(database, fixture, "viewer", "tenantA", PermissionSet.ViewerId, now);
        AddMember(database, fixture, "eligibleTarget", "tenantA", PermissionSet.ViewerId, now);
        AddMember(database, fixture, "ineligibleTarget", "tenantA", PermissionSet.ViewerId, now);
        AddMember(database, fixture, "crossTenantTarget", "tenantB", PermissionSet.ViewerId, now);
        foreach (var suffix in new[] { "A", "B" })
            database.OrganisationMemberships.Add(OrganisationMembership.Create(
                Id(fixture["operator"]!, "membership" + suffix + "Id"), Id(fixture, "tenant" + suffix),
                Id(fixture["operator"]!, "userId"), PermissionSet.OrganisationAdministratorId, now));
        await database.SaveChangesAsync(token);
        // Credentials never enter the public manifest, native output, shared evidence or containers.
        await run.WritePrivate("Auth/Session/credentials.json", credentials, token, replace: false);
        await transaction.CommitAsync(token);
        await run.UpdateManifest(doc => doc["fixtures"]!["authState"] = "initialized", token);
        await run.Receipt("authInitialized", token);
    });

    [Fact] public Task Readback() => Run("Readback", async (run, token) =>
    {
        var fixture = run.Fixtures();
        await using var database = await run.Runtime(token);
        await VerifyOwnedRows(run, database, fixture, token);
        foreach (var name in UserNames)
        {
            var subject = Id(fixture[name]!, "userId");
            var user = await database.Users.AsNoTracking().SingleAsync(x => x.Id == subject, token);
            Require(user.EmailConfirmed && user.Status == UserStatus.Active && !string.IsNullOrEmpty(user.PasswordHash)
                && !string.IsNullOrEmpty(user.SecurityStamp), "Synthetic Identity readback failed.");
        }
        foreach (var suffix in new[] { "A", "B" })
        {
            var org = await database.Organisations.AsNoTracking().SingleAsync(x => x.Id == Id(fixture, "tenant" + suffix), token);
            Require(org.Status == OrganisationStatus.Active && org.TimeZoneId == "Europe/Brussels"
                && org.TimeZoneConfigVersion == 1, "Synthetic organisation/timezone readback failed.");
        }
        await CheckPermission(run, fixture, "editor", "tenantA", "Clients.EditAll", true, token);
        await CheckPermission(run, fixture, "viewer", "tenantA", "Clients.EditAll", false, token);
        await CheckPermission(run, fixture, "viewer", "tenantA", "Clients.ViewAll", true, token);
        await CheckPermission(run, fixture, "operator", "tenantA", "TeamConfiguration.ManageStaffProfiles", true, token);
        await CheckPermission(run, fixture, "operator", "tenantB", "TeamConfiguration.ManageStaffProfiles", true, token);
        await CheckPermission(run, fixture, "crossTenantTarget", "tenantA", "Clients.ViewAll", false, token);
        await CheckPermission(run, fixture, "ineligibleTarget", "tenantA", "Clients.ViewAll", true, token);
        await CheckEligibility(run, fixture, "eligibleTarget", true, token);
        await CheckEligibility(run, fixture, "crossTenantTarget", false, token);
        await run.Receipt("authReadbackVerified", token);
    });

    [Fact] public Task RevokeAccess() => Run("RevokeAccess", async (run, token) =>
    {
        var fixture = run.Fixtures();
        await using var database = await run.Runtime(token);
        await VerifyOwnedRows(run, database, fixture, token);
        var target = Environment.GetEnvironmentVariable("ZEKA_B2_REVOKE_TARGET");
        Require(target is "editor" or "viewer" or "eligibleTarget" or "ineligibleTarget" or "crossTenantTarget", "Exact run-owned revocation target is required.");
        var id = Id(fixture[target!]!, "membershipId");
        var tenant = target == "crossTenantTarget" ? "tenantB" : "tenantA";
        var organisationId = Id(fixture, tenant);
        var subjectId = Id(fixture[target!]!, "userId");
        var membership = await database.OrganisationMemberships.SingleAsync(x => x.Id == id
            && x.OrganisationId == organisationId && x.UserId == subjectId, token);
        Require(membership.Suspend(DateTimeOffset.UtcNow), "Run-owned access is already revoked; do not retry automatically.");
        await database.SaveChangesAsync(token);
        await CheckPermission(run, fixture, target!, tenant, "Clients.ViewAll", false, token);
        if (target is "eligibleTarget" or "ineligibleTarget") await CheckEligibility(run, fixture, target, false, token);
        await run.Receipt(target + "AccessRevoked", token);
    });

    [Fact] public Task RevokeSessions() => Run("RevokeSessions", async (run, token) =>
    {
        await using var database = await run.Runtime(token);
        var fixture = run.Fixtures();
        await VerifyOwnedRows(run, database, fixture, token);
        await RevokeOwnedSessions(database, fixture, token);
        await run.Receipt("sessionsRevoked", token);
    });

    [Fact] public Task Teardown() => Run("Teardown", async (run, token) =>
    {
        var fixture = run.Fixtures();
        await using var database = await run.Runtime(token);
        await VerifyOwnedRows(run, database, fixture, token);
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await RevokeOwnedSessions(database, fixture, token);
        var userIds = UserNames.Select(name => Id(fixture[name]!, "userId")).ToArray();
        var orgIds = new[] { Id(fixture, "tenantA"), Id(fixture, "tenantB") };
        var memberships = await database.OrganisationMemberships.Where(x => orgIds.Contains(x.OrganisationId)
            && userIds.Contains(x.UserId)).ToListAsync(token);
        var now = DateTimeOffset.UtcNow;
        foreach (var membership in memberships)
            if (membership.Status == MembershipStatus.Active) Require(membership.Suspend(now), "Run-owned membership teardown failed.");
        foreach (var org in await database.Organisations.Where(x => orgIds.Contains(x.Id)).ToListAsync(token))
            if (org.Status is OrganisationStatus.Active or OrganisationStatus.Pending or OrganisationStatus.Suspended)
                Require(org.Close(now), "Run-owned organisation teardown failed.");
        foreach (var user in await database.Users.Where(x => userIds.Contains(x.Id)).ToListAsync(token))
        {
            user.LockoutEnd = DateTimeOffset.MaxValue;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        }
        // Audit and grant history remain append-preserving. Cleanup never deletes shared data.
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        await run.UpdateManifest(doc => doc["fixtures"]!["authState"] = "revoked", token);
        await run.Receipt("authTeardownVerified", token);
    });

    [Fact] public Task InitializeTimeZoneTEST() => Run("InitializeTimeZoneTEST", async (run, token) =>
    {
        Require(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEKA_B2_AUTH_RUNTIME_CONNECTION")),
            "TEST writer must run in a separate process without runtime credentials.");
        var connection = Environment.GetEnvironmentVariable("ZEKA_B2_AUTH_SETUP_CONNECTION");
        ValidateConnection(connection, "zeka_auth_migrator");
        var fixture = run.Fixtures();
        var allowed = run.Manifest["fixtureTimeZone"]!["newOrganisationIds"]!.AsArray().Select(x => Guid.Parse(x!.GetValue<string>())).ToArray();
        Require(allowed.Length == 2 && allowed.Distinct().Count() == 2
            && allowed.Contains(Id(fixture, "tenantA")) && allowed.Contains(Id(fixture, "tenantB")), "TEST writer organisation allowlist mismatch.");
        // Ephemeral TEST trust is local to this process. It is never enrolled in the application.
        using var rsa = RSA.Create(3072);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "TEST-only-" + run.RunId };
        await run.WritePrivate("Auth/Writer/ephemeral-test-signing.pem", rsa.ExportRSAPrivateKeyPem(), token, replace: false);
        foreach (var organisationId in allowed)
        {
            await using var database = new AuthDbContext(Options(connection!));
            await VerifyDatabaseIdentity(database, "zeka_auth_migrator", token);
            await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner", token);
            var organisation = await database.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisationId, token);
            Require(organisation.TimeZoneId is null && organisation.TimeZoneConfigVersion == 0
                && organisation.Status == OrganisationStatus.Active, "TEST writer requires a newly initialized, unconfigured run-owned organisation.");
            Require(organisation.DisplayName == $"Synthetic B2 {run.RunId} {(organisationId == Id(fixture, "tenantA") ? "A" : "B")}"
                && organisation.OwnerUserId == (organisationId == Id(fixture, "tenantA")
                ? Id(fixture["ownerA"]!, "userId") : Id(fixture["ownerB"]!, "userId")), "TEST writer owner mismatch.");
            var now = DateTimeOffset.UtcNow;
            var command = new ConfigureTimeZoneCommand("Development", organisationId, "Europe/Brussels", 0,
                Guid.NewGuid(), "ZEK-16/#97 TEST synthetic fixture " + run.RunId);
            var proof = new HumanTimeZoneReleaseProof("TEST-synthetic-executor", "TEST-synthetic-reviewer",
                "TEST-only-approved-setup-annex", command.ArtifactSha256(), run.RunId);
            var claims = new[]
            {
                new Claim("purpose", "ConfigureTimeZone"), new Claim("environment", command.Environment),
                new Claim("organisationId", organisationId.ToString("D")), new Claim("timeZoneId", command.TimeZoneId),
                new Claim("expectedConfigVersion", "0"), new Claim("approvalId", command.ApprovalId.ToString("D")),
                new Claim("changeReference", command.ChangeReference), new Claim("artifactSha256", command.ArtifactSha256()),
                new Claim("ownerSubject", "TEST-synthetic-owner")
            };
            var compact = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(claims: claims,
                notBefore: now.AddSeconds(-1).UtcDateTime, expires: now.AddMinutes(2).UtcDateTime,
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
            await run.WritePrivate($"Auth/Writer/{organisationId:D}-approval.json",
                new { testOnly = true, command, proof, ownerJws = compact }, token, replace: false);
            var verifier = new ConfigureTimeZoneApprovalVerifier(new Dictionary<string, SecurityKey>
                { [signingKey.KeyId] = new RsaSecurityKey(rsa.ExportParameters(false)) }, TimeProvider.System);
            var core = new ConfigureTimeZoneCore(database, verifier, TimeProvider.System);
            Require(await core.ExecuteAsync(compact, command, proof, token) == ConfigureTimeZoneOutcome.Committed,
                "TEST timezone core did not commit; never retry an uncertain approval.");
            Require(await core.ReconcileAsync(command, token) == ConfigureTimeZoneOutcome.Committed,
                "TEST timezone core receipt reconciliation failed.");
        }
        await run.Receipt("testTimeZonesInitialized", token);
    });

    private static async Task Run(string operation, Func<RunContext, CancellationToken, Task> action)
    {
        var selected = Environment.GetEnvironmentVariable("ZEKA_B2_OPERATION");
        var path = Environment.GetEnvironmentVariable("ZEKA_B2_MANIFEST");
        if (selected != operation) return;
        Require(!string.IsNullOrWhiteSpace(path), "Selected operation requires an explicit run manifest.");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var run = RunContext.Load(path!);
            if (operation != "InitializeTimeZoneTEST")
                Require(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ZEKA_B2_AUTH_SETUP_CONNECTION")),
                    "Runtime fixture process must not receive privileged setup credentials.");
            await action(run, deadline.Token);
        }
        catch (Exception failure)
        {
            // Never export provider details, configuration values, private input or an inner exception to TRX.
            throw new InvalidOperationException($"Auth B2 {operation} failed ({failure.GetType().Name}); retain owned state for bounded review.");
        }
    }

    private static bool IsSelected(string? selected, string? manifest, string operation) =>
        selected == operation && !string.IsNullOrWhiteSpace(manifest);

    private static JsonObject NewFixture()
    {
        static string NewId() => Guid.NewGuid().ToString("D");
        var a = NewId(); var b = NewId();
        var fixture = new JsonObject { ["tenantA"] = a, ["tenantB"] = b,
            ["newOrganisationIds"] = new JsonArray(JsonValue.Create(a), JsonValue.Create(b)), ["authState"] = "planned" };
        foreach (var name in UserNames)
            fixture[name] = new JsonObject { ["userId"] = NewId(), ["membershipId"] = NewId() };
        fixture["operator"] = new JsonObject { ["userId"] = NewId(), ["membershipAId"] = NewId(), ["membershipBId"] = NewId() };
        return fixture;
    }

    private static OrganisationMembership AddMember(AuthDbContext database, JsonObject fixture, string name,
        string tenant, Guid role, DateTimeOffset now)
    {
        var member = OrganisationMembership.Create(Id(fixture[name]!, "membershipId"), Id(fixture, tenant),
            Id(fixture[name]!, "userId"), role, now);
        database.OrganisationMemberships.Add(member);
        return member;
    }

    private static async Task CheckPermission(RunContext run, JsonObject fixture, string name, string tenant,
        string permission, bool expected, CancellationToken token)
    {
        await using var database = await run.Runtime(token);
        var actual = await new CurrentTenantAccessResolver(database, TimeProvider.System)
            .ResolveAsync(Id(fixture[name]!, "userId"), Id(fixture, tenant), token);
        Require((actual.Outcome == TenantAccessOutcome.Authorized && actual.EffectivePermissionCodes.Contains(permission)) == expected,
            "Real current tenant permission readback mismatch.");
    }

    private static async Task CheckEligibility(RunContext run, JsonObject fixture, string target, bool expected, CancellationToken token)
    {
        await using var database = await run.Runtime(token);
        var request = new AssigneeEligibilityRequest(1, Id(fixture, "tenantA"),
            Id(fixture["editor"]!, "userId"), Id(fixture["editor"]!, "membershipId"), Id(fixture[target]!, "membershipId"));
        var decision = await new AssigneeEligibilityResolver(database, TimeProvider.System).ResolveAsync(request, token);
        Require((decision is not null) == expected, "Real Auth assignee eligibility readback mismatch.");
    }
    private static async Task VerifyOwnedRows(RunContext run, AuthDbContext database, JsonObject fixture, CancellationToken token)
    {
        foreach (var name in UserNames)
        {
            var userId = Id(fixture[name]!, "userId");
            var expectedEmail = $"b2-{run.RunId}-{name}@example.invalid";
            Require(await database.Users.AsNoTracking().AnyAsync(x => x.Id == userId && x.Email == expectedEmail, token),
                "Fixture subject does not belong to this synthetic run.");
        }
        foreach (var suffix in new[] { "A", "B" })
        {
            var organisationId = Id(fixture, "tenant" + suffix);
            var ownerId = Id(fixture["owner" + suffix]!, "userId");
            var expectedName = $"Synthetic B2 {run.RunId} {suffix}";
            Require(await database.Organisations.AsNoTracking().AnyAsync(x => x.Id == organisationId
                && x.OwnerUserId == ownerId && x.DisplayName == expectedName, token),
                "Fixture organisation does not belong to this synthetic run.");
        }
    }
    private static async Task RevokeOwnedSessions(AuthDbContext database, JsonObject fixture, CancellationToken token)
    {
        var users = UserNames.Select(name => Id(fixture[name]!, "userId")).ToArray();
        var families = await database.BrowserSessionFamilies.Where(x => users.Contains(x.UserId) && x.RevokedAtUtc == null).ToListAsync(token);
        foreach (var family in families) family.RevokedAtUtc = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(token);
        Require(!await database.BrowserSessionFamilies.AnyAsync(x => users.Contains(x.UserId) && x.RevokedAtUtc == null, token),
            "Run-owned session revocation readback failed.");
    }

    private static void ValidateIdentity(string? environment, bool exclusiveTarget, string? source) =>
        Require(environment == "Development" && exclusiveTarget && source == SourceCommit,
            "Approved source, Development or exclusive-target admission failed.");

    private static void ValidateConnection(string? connection, string role)
    {
        Require(!string.IsNullOrWhiteSpace(connection), "Required Auth connection is absent.");
        var builder = new NpgsqlConnectionStringBuilder(connection);
        Require(builder.Host == "127.0.0.1" && builder.Port == 5432 && builder.Database == "zekaAuthdb"
            && builder.Username == role && string.IsNullOrEmpty(builder.Options)
            && string.IsNullOrEmpty(builder.SearchPath) && !builder.Multiplexing && !builder.IncludeErrorDetail
            && !builder.PersistSecurityInfo, "Auth database target or role mismatch.");
    }

    private static DbContextOptions<AuthDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection,
            options => options.CommandTimeout(15)).Options;

    private static async Task VerifyDatabaseIdentity(AuthDbContext database, string role, CancellationToken token)
    {
        await database.Database.OpenConnectionAsync(token);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandTimeout = 15;
        command.CommandText = """
            SELECT current_database() = 'zekaAuthdb' AND current_user = @role AND session_user = @role
                AND NOT r.rolsuper AND NOT r.rolbypassrls AND NOT r.rolcreatedb AND NOT r.rolcreaterole
                AND NOT r.rolreplication AND NOT r.rolinherit
            FROM pg_catalog.pg_roles r WHERE r.rolname = current_user
            """;
        var parameter = command.CreateParameter(); parameter.ParameterName = "role"; parameter.Value = role;
        command.Parameters.Add(parameter);
        Require(Equals(await command.ExecuteScalarAsync(token), true), "Actual Auth database identity is not admitted.");
        if (role == "zeka_auth_runtime")
        {
            command.Parameters.Clear();
            command.CommandText = """
                SELECT NOT pg_catalog.pg_has_role(current_user, 'zeka_auth_owner', 'MEMBER')
                    AND NOT pg_catalog.has_column_privilege(current_user, 'public."Organisations"', 'TimeZoneId', 'UPDATE')
                    AND NOT pg_catalog.has_column_privilege(current_user, 'public."Organisations"', 'TimeZoneConfigVersion', 'UPDATE')
                """;
            Require(Equals(await command.ExecuteScalarAsync(token), true), "Runtime is privileged or can configure timezones.");
        }
    }

    private static Guid Id(JsonNode node, string key)
    {
        Require(Guid.TryParse(node[key]?.GetValue<string>(), out var value) && value != Guid.Empty,
            "Run-owned fixture identifier is invalid.");
        return value;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RunContext(string manifestPath, JsonObject manifest)
    {
        public JsonObject Manifest { get; private set; } = manifest;
        public string RunId => Manifest["runId"]!.GetValue<string>();
        private string Root => Path.GetDirectoryName(manifestPath)!;
        private string PrivateRoot => Path.Combine(Root, "private");

        public static RunContext Load(string path)
        {
            path = Path.GetFullPath(path);
            Require(Path.GetFileName(path) == "manifest.json" && File.Exists(path), "Exact run manifest is required.");
            RejectLinks(path);
            var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            ValidateIdentity(manifest["environment"]?.GetValue<string>(), manifest["exclusiveTarget"]?.GetValue<bool>() == true,
                manifest["sourceBaseline"]?.GetValue<string>());
            Require(manifest["schemaVersion"]?.GetValue<int>() == 1
                && Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development", "Manifest schema or process environment mismatch.");
            Require(manifest["sourceCommit"]?.GetValue<string>() is { Length: 40 } candidate
                && candidate.All(Uri.IsHexDigit) && candidate == Environment.GetEnvironmentVariable("ZEKA_B2_SOURCE_COMMIT"),
                "Runtime candidate source identity mismatch.");
            var timeZone = manifest["fixtureTimeZone"];
            Require(timeZone?["mode"]?.GetValue<string>() == "ISOLATED_SYNTHETIC_TEST"
                && timeZone["initializationApproved"]?.GetValue<bool>() == true, "TEST timezone annex admission is absent.");
            var run = new RunContext(path, manifest);
            Require(run.RunId.Length is > 0 and <= 64 && run.RunId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "Run identifier is invalid.");
            Require(string.Equals(Path.GetFullPath(manifest["privateRoot"]!.GetValue<string>()), run.Root, StringComparison.OrdinalIgnoreCase)
                && string.Equals(run.Root, Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Zeka", "B2", run.RunId)), StringComparison.OrdinalIgnoreCase), "Private custody root mismatch.");
            var postgres = manifest["postgres"]!;
            Require(postgres["host"]?.GetValue<string>() == "127.0.0.1" && postgres["port"]?.GetValue<int>() == 5432
                && postgres["authDatabase"]?.GetValue<string>() == "zekaAuthdb"
                && !string.IsNullOrEmpty(postgres["containerId"]?.GetValue<string>())
                && postgres["containerId"]!.GetValue<string>() == Environment.GetEnvironmentVariable("ZEKA_B2_POSTGRES_CONTAINER_ID"),
                "Selected PostgreSQL source identity mismatch.");
            var auth = manifest["auth"]!;
            Require(auth["baseUrl"]?.GetValue<string>() == "https://localhost:19444"
                && auth["origin"]?.GetValue<string>() == "https://localhost:19443"
                && auth["issuer"]?.GetValue<string>() == "https://authman.api:8443"
                && auth["audience"]?.GetValue<string>() == "zeka-api-local-b2", "Approved Auth origin/issuer contract mismatch.");
            Directory.CreateDirectory(run.PrivateRoot);
            RejectLinks(run.PrivateRoot);
            return run;
        }

        public JsonObject Fixtures()
        {
            var fixture = Manifest["fixtures"]?.AsObject();
            Require(fixture is not null && fixture["authState"]?.GetValue<string>() == "initialized", "Auth fixtures are not initialized for this run.");
            var ids = UserNames.Select(x => Id(fixture![x]!, "userId")).ToArray();
            Require(ids.Distinct().Count() == UserNames.Length, "Fixture subjects are not distinct.");
            return fixture!;
        }

        public async Task<AuthDbContext> Runtime(CancellationToken token)
        {
            var connection = Environment.GetEnvironmentVariable("ZEKA_B2_AUTH_RUNTIME_CONNECTION");
            ValidateConnection(connection, "zeka_auth_runtime");
            var database = new AuthDbContext(Options(connection!));
            try { await VerifyDatabaseIdentity(database, "zeka_auth_runtime", token); return database; }
            catch { await database.DisposeAsync(); throw; }
        }

        public async Task Receipt(string key, CancellationToken token) => await UpdateManifest(doc =>
        {
            doc["authReceipts"] ??= new JsonObject();
            doc["authReceipts"]![key] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }, token);

        public async Task UpdateManifest(Action<JsonObject> update, CancellationToken token)
        {
            var lockPath = Path.Combine(PrivateRoot, "manifest.lock");
            await using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            RejectLinks(manifestPath);
            var latest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token))!.AsObject();
            Require(latest["runId"]!.GetValue<string>() == RunId && latest["sourceBaseline"]!.GetValue<string>() == SourceCommit
                && latest["sourceCommit"]!.GetValue<string>() == Environment.GetEnvironmentVariable("ZEKA_B2_SOURCE_COMMIT"),
                "Manifest changed ownership during operation.");
            update(latest);
            var scratch = manifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(scratch, latest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
                File.Move(scratch, manifestPath, true);
                Manifest = latest;
            }
            finally { if (File.Exists(scratch)) File.Delete(scratch); }
        }

        public Task WritePrivate(string relativePath, object value, CancellationToken token, bool replace) =>
            WritePrivate(relativePath, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), token, replace);

        public async Task WritePrivate(string relativePath, string value, CancellationToken token, bool replace)
        {
            var path = Path.GetFullPath(Path.Combine(PrivateRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            Require(path.StartsWith(PrivateRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Private output escaped its custody root.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RejectLinks(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, replace ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(value.AsMemory(), token);
        }

        private static void RejectLinks(string path)
        {
            for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (File.Exists(current) || Directory.Exists(current))
                    Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "Run custody path contains a link.");
        }
    }
}
