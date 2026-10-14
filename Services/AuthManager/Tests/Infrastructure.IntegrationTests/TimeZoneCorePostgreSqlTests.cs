using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AuthManager.Core.Organisations;
using AuthManager.Core.Enums;
using AuthManager.Application.Authorization;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.TimeZones;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "78")]
public sealed class TimeZoneCorePostgreSqlTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.Zero);
    private const string RuntimePassword = "test-timezone-runtime-password";

    [Fact]
    public async Task New_organisations_remain_unconfigured_and_runtime_cannot_write_zone()
    {
        var connection = await PreparedDatabaseAsync();
        await using var db = Context(connection);
        var organisationId = await AddActiveOrganisationAsync(db);
        var actor = await db.Users.AsNoTracking().SingleAsync();
        var membership = await db.OrganisationMemberships.AsNoTracking().SingleAsync();
        var resolver = new OrganisationTimeZoneResolver(db, new FixedClock(Now));
        var request = new OrganisationTimeZoneRequest(1, organisationId, actor.Id, membership.Id);
        (await resolver.ResolveAsync(request, default)).Should().BeNull();
        var organisation = await db.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisationId);
        organisation.TimeZoneId.Should().BeNull();
        organisation.TimeZoneConfigVersion.Should().Be(0);

        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        await using var transaction = await sql.BeginTransactionAsync();
        await using var command = sql.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SET LOCAL ROLE zeka_auth_runtime;
            SELECT pg_catalog.set_config('zeka.organisation_id', '{organisationId:D}', true);
            UPDATE public."Organisations" SET "TimeZoneId" = 'Europe/Brussels',
              "TimeZoneConfigVersion" = 1 WHERE "Id" = '{organisationId:D}';
            """;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Additive_migration_leaves_preexisting_organisation_unconfigured()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await BootstrapAsync(connection);
        await using var db = Context(connection);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260929222145_Life05aVerificationFoundation");
        var user = User.Create("premigration-owner@example.invalid", "premigration-owner",
            "Pre", "Migration", Now);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var organisation = Organisation.Create(Guid.NewGuid(), "Preexisting", user.Id, Now);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."Organisations" ("Id", "DisplayName", "OwnerUserId", "Status", "Slug",
              "CreatedAtUtc", "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES ({organisation.Id}, {organisation.DisplayName}, {organisation.OwnerUserId},
              {(int)organisation.Status}, {organisation.Slug}, {organisation.CreatedAtUtc},
              {organisation.UpdatedAtUtc}, {(long)organisation.ConcurrencyVersion})
            """);
        await migrator.MigrateAsync();
        await BootstrapAsync(connection);
        var persisted = await db.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisation.Id);
        persisted.TimeZoneId.Should().BeNull();
        persisted.TimeZoneConfigVersion.Should().Be(0);
        await AssertPrivilegeBoundaryAsync(connection, "after additive migration and reconciliation");
    }

    [Fact]
    public async Task Restricted_runtime_cannot_insert_or_update_either_timezone_column_but_can_mutate_legacy_organisation()
    {
        var connection = await PreparedDatabaseAsync();
        var organisationId = Guid.NewGuid();
        var runtimeConnection = new NpgsqlConnectionStringBuilder(connection)
        {
            Username = "zeka_auth_runtime", Password = RuntimePassword, Pooling = false
        }.ConnectionString;
        await using (var administrator = Context(connection))
        {
            administrator.Users.Add(User.Create("legacy-owner@example.invalid", "legacy-owner",
                "Legacy", "Owner", Now));
            await administrator.SaveChangesAsync();
        }

        Guid ownerId;
        await using (var administrator = Context(connection))
            ownerId = await administrator.Users.AsNoTracking()
                .Where(x => x.UserName == "legacy-owner")
                .Select(x => x.Id).SingleAsync();

        await using (var runtime = Context(runtimeConnection))
        {
            await runtime.Database.OpenConnectionAsync();
            await runtime.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_catalog.set_config('zeka.organisation_id', {organisationId.ToString("D")}, false)");
            var organisation = Organisation.Create(organisationId, "Legacy organisation", ownerId, Now);
            runtime.Organisations.Add(organisation);
            await runtime.SaveChangesAsync();
            organisation.Activate(Now.AddSeconds(1)).Should().BeTrue();
            await runtime.SaveChangesAsync();
        }

        await using var sql = new NpgsqlConnection(runtimeConnection);
        await sql.OpenAsync();
        await using (var setTenant = sql.CreateCommand())
        {
            setTenant.CommandText = "SELECT pg_catalog.set_config('zeka.organisation_id', @tenant, false)";
            setTenant.Parameters.AddWithValue("tenant", organisationId.ToString("D"));
            await setTenant.ExecuteNonQueryAsync();
        }
        foreach (var column in new[] { "TimeZoneId", "TimeZoneConfigVersion" })
        {
            foreach (var verb in new[] { "INSERT", "UPDATE" })
            {
                await using var command = sql.CreateCommand();
                command.CommandText = verb == "INSERT"
                    ? $"INSERT INTO public.\"Organisations\" (\"{column}\") VALUES (DEFAULT)"
                    : $"UPDATE public.\"Organisations\" SET \"{column}\" = DEFAULT WHERE \"Id\" = @id";
                if (verb == "UPDATE") command.Parameters.AddWithValue("id", organisationId);
                var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    $"runtime {verb} on {column} must be denied");
            }
        }
        await using (var command = sql.CreateCommand())
        {
            command.CommandText = "INSERT INTO public.\"TimeZoneConfigurationAudits\" DEFAULT VALUES";
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }
        await using (var command = sql.CreateCommand())
        {
            command.CommandText = "SELECT count(*) FROM public.\"TimeZoneConfigurationAudits\"";
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "approval receipts are unavailable to the ordinary runtime");
        }
    }

    [Fact]
    public async Task Synthetic_signed_approval_commits_once_with_audit_and_reconciles()
    {
        var connection = await PreparedDatabaseAsync();
        await using var db = Context(connection);
        var organisationId = await AddActiveOrganisationAsync(db);
        var actor = await db.Users.AsNoTracking().SingleAsync();
        var membership = await db.OrganisationMemberships.AsNoTracking().SingleAsync();
        var resolver = new OrganisationTimeZoneResolver(db, new FixedClock(Now));
        var request = new OrganisationTimeZoneRequest(1, organisationId, actor.Id, membership.Id);
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-key" };
        var clock = new FixedClock(Now);
        var verifier = new ConfigureTimeZoneApprovalVerifier(
            new Dictionary<string, SecurityKey> { [key.KeyId] = key }, clock);
        var core = new ConfigureTimeZoneCore(db, verifier, clock);
        var command = new ConfigureTimeZoneCommand("test", organisationId,
            "Europe/Brussels", 0, Guid.NewGuid(), "test-change");
        var jws = SignedApproval(command, key);
        var human = new HumanTimeZoneReleaseProof("executor-test", "reviewer-test",
            "release-test", command.ArtifactSha256(), "correlation-test");

        (await core.ExecuteAsync(jws, command, human, default))
            .Should().Be(ConfigureTimeZoneOutcome.Committed);
        (await core.ReconcileAsync(command, default)).Should().Be(ConfigureTimeZoneOutcome.Committed);
        (await core.ExecuteAsync(jws, command, human, default))
            .Should().Be(ConfigureTimeZoneOutcome.Denied);
        var organisation = await db.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisationId);
        organisation.TimeZoneId.Should().Be("Europe/Brussels");
        organisation.TimeZoneConfigVersion.Should().Be(1);
        (await db.TimeZoneConfigurationAudits.CountAsync(x => x.ApprovalId == command.ApprovalId))
            .Should().Be(1);
        var decision = await resolver.ResolveAsync(request, default);
        decision.Should().NotBeNull();
        decision!.TimeZoneId.Should().Be("Europe/Brussels");
        decision.ConfigVersion.Should().Be(1);
        (await resolver.ResolveAsync(request with { ActorMembershipId = Guid.NewGuid() }, default))
            .Should().BeNull();
        (await resolver.ResolveAsync(request with { OrganisationId = Guid.NewGuid() }, default))
            .Should().BeNull();
        var trackedMembership = await db.OrganisationMemberships.SingleAsync(x => x.Id == membership.Id);
        trackedMembership.Suspend(Now.AddSeconds(2));
        await db.SaveChangesAsync();
        (await resolver.ResolveAsync(request, default)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_redemptions_commit_one_receipt_and_one_version_increment()
    {
        var connection = await PreparedDatabaseAsync();
        await using (var arrange = Context(connection))
            await AddActiveOrganisationAsync(arrange);
        Guid organisationId;
        await using (var read = Context(connection))
            organisationId = await read.Organisations.Select(x => x.Id).SingleAsync();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-key" };
        var command = new ConfigureTimeZoneCommand("test", organisationId,
            "Europe/Brussels", 0, Guid.NewGuid(), "test-concurrent-change");
        var jws = SignedApproval(command, key);
        var human = new HumanTimeZoneReleaseProof("executor-test", "reviewer-test",
            "release-test", command.ArtifactSha256(), "correlation-test");
        await using var first = Context(connection);
        await using var second = Context(connection);
        var verifier = new ConfigureTimeZoneApprovalVerifier(
            new Dictionary<string, SecurityKey> { [key.KeyId] = key }, new FixedClock(Now));
        var results = await Task.WhenAll(
            new ConfigureTimeZoneCore(first, verifier, new FixedClock(Now))
                .ExecuteAsync(jws, command, human, default),
            new ConfigureTimeZoneCore(second, verifier, new FixedClock(Now))
                .ExecuteAsync(jws, command, human, default));
        results.Count(x => x == ConfigureTimeZoneOutcome.Committed).Should().Be(1);
        await using var verification = Context(connection);
        (await verification.TimeZoneConfigurationAudits.CountAsync(x => x.ApprovalId == command.ApprovalId))
            .Should().Be(1);
        (await verification.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisationId))
            .TimeZoneConfigVersion.Should().Be(1);
    }

    [Fact]
    public async Task Approval_and_human_binding_fail_closed_without_consuming_a_receipt()
    {
        var connection = await PreparedDatabaseAsync();
        await using var db = Context(connection);
        var organisationId = await AddActiveOrganisationAsync(db);
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-key" };
        var clock = new FixedClock(Now);
        var verifier = new ConfigureTimeZoneApprovalVerifier(
            new Dictionary<string, SecurityKey> { [key.KeyId] = key }, clock);
        var command = new ConfigureTimeZoneCommand("test", organisationId,
            "Europe/Brussels", 0, Guid.NewGuid(), "test-negative-change");
        var jws = SignedApproval(command, key);
        (await verifier.VerifyAsync(jws, command with { OrganisationId = Guid.NewGuid() }))
            .Should().BeNull();
        (await new ConfigureTimeZoneApprovalVerifier(
            new Dictionary<string, SecurityKey>(), clock).VerifyAsync(jws, command))
            .Should().BeNull();
        (await new ConfigureTimeZoneApprovalVerifier(
            new Dictionary<string, SecurityKey> { [key.KeyId] = key },
            new FixedClock(Now.AddMinutes(2))).VerifyAsync(jws, command))
            .Should().BeNull();
        ConfigureTimeZoneApprovalVerifier.IsCanonicalIana("US/Eastern").Should().BeFalse();
        var human = new HumanTimeZoneReleaseProof("owner-test", "reviewer-test",
            "release-test", command.ArtifactSha256(), "correlation-test");
        (await new ConfigureTimeZoneCore(db, verifier, clock)
            .ExecuteAsync(jws, command, human, default)).Should().Be(ConfigureTimeZoneOutcome.Denied);
        var selfReviewed = human with
        { ExecutorSubject = "executor-test", ReviewerSubject = "executor-test" };
        (await new ConfigureTimeZoneCore(db, verifier, clock)
            .ExecuteAsync(jws, command, selfReviewed, default)).Should().Be(ConfigureTimeZoneOutcome.Denied);
        (await db.TimeZoneConfigurationAudits.CountAsync()).Should().Be(0);
        (await db.Organisations.AsNoTracking().SingleAsync(x => x.Id == organisationId))
            .TimeZoneConfigVersion.Should().Be(0);
    }

    private async Task<string> PreparedDatabaseAsync()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await BootstrapAsync(connection);
        await using (var sql = new NpgsqlConnection(connection))
        {
            await sql.OpenAsync();
            await using var command = sql.CreateCommand();
            command.CommandText = $"ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}'";
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Context(connection);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
        await db.GetService<IMigrator>().MigrateAsync();
        await AssertPrivilegeBoundaryAsync(connection, "after migration");
        await BootstrapAsync(connection);
        await AssertPrivilegeBoundaryAsync(connection, "after reconciliation");
        await BootstrapAsync(connection);
        await AssertPrivilegeBoundaryAsync(connection, "after repeated reconciliation");
        return connection;
    }

    private static async Task BootstrapAsync(string connection)
    {
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
                   "Deployments", "database", "bootstrap-auth-roles.sql")))
            directory = directory.Parent;
        command.CommandText = File.ReadAllText(Path.Combine(directory!.FullName,
            "Deployments", "database", "bootstrap-auth-roles.sql"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertPrivilegeBoundaryAsync(string connection, string phase)
    {
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        command.CommandText = """
            SELECT NOT pg_catalog.has_table_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'INSERT')
              AND NOT pg_catalog.has_table_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'UPDATE')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'SELECT')
              AND pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'DisplayName', 'INSERT')
              AND pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'Status', 'UPDATE')
              AND NOT pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'TimeZoneId', 'INSERT')
              AND NOT pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'TimeZoneId', 'UPDATE')
              AND NOT pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'TimeZoneConfigVersion', 'INSERT')
              AND NOT pg_catalog.has_column_privilege('zeka_auth_runtime',
                       'public."Organisations"', 'TimeZoneConfigVersion', 'UPDATE')
            """;
        ((bool)(await command.ExecuteScalarAsync())!).Should().BeTrue(phase);
    }

    private static AuthDbContext Context(string connection) => new(
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);

    private static async Task<Guid> AddActiveOrganisationAsync(AuthDbContext db)
    {
        var user = User.Create("timezone-owner@example.org", "timezone-owner", "Ada", "Lovelace", Now);
        user.EmailConfirmed = true;
        var organisation = Organisation.Create(Guid.NewGuid(), "Timezone test", user.Id, Now);
        organisation.Activate(Now.AddSeconds(1));
        db.Users.Add(user);
        db.Organisations.Add(organisation);
        db.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(
            Guid.NewGuid(), organisation.Id, user.Id, Now));
        db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Active;
        await db.SaveChangesAsync();
        return organisation.Id;
    }

    private static string SignedApproval(ConfigureTimeZoneCommand command, SecurityKey key)
    {
        var claims = new[]
        {
            new Claim("purpose", "ConfigureTimeZone"), new Claim("environment", command.Environment),
            new Claim("organisationId", command.OrganisationId.ToString("D")),
            new Claim("timeZoneId", command.TimeZoneId),
            new Claim("expectedConfigVersion", command.ExpectedConfigVersion.ToString()),
            new Claim("approvalId", command.ApprovalId.ToString("D")),
            new Claim("changeReference", command.ChangeReference),
            new Claim("artifactSha256", command.ArtifactSha256()),
            new Claim("ownerSubject", "owner-test")
        };
        var token = new JwtSecurityToken(claims: claims,
            notBefore: Now.AddMinutes(-1).UtcDateTime,
            expires: Now.AddMinutes(1).UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
