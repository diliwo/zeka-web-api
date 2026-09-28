using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "46")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class PostgreSqlMembershipPermissionGrantTests(PostgreSqlFixture fixture)
{
    private const string MigratorPassword = "test-grant-migrator-password";
    private const string RuntimePassword = "test-grant-runtime-password";
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Owner_controlled_grant_and_revoke_extend_provider_real_effective_permissions()
    {
        var environment = await CreateEnvironmentAsync();
        await using var database = new AuthDbContext(Options(environment.RuntimeConnection));
        var clock = new ManualTimeProvider(Now.AddMinutes(1));
        var application = new MembershipPermissionGrants(
            new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), clock));
        var resolver = new CurrentTenantAccessResolver(database, clock);

        var owner = await resolver.ResolveAsync(environment.OwnerUserId, environment.OrganisationId);
        Assert.Contains("Organisations.Export", owner.EffectivePermissionCodes);
        var ordinaryAdmin = await resolver.ResolveAsync(environment.AdminUserId, environment.OrganisationId);
        Assert.DoesNotContain("Organisations.Export", ordinaryAdmin.EffectivePermissionCodes);
        Assert.Contains("Clients.Delete", ordinaryAdmin.EffectivePermissionCodes);

        var request = environment.Request(environment.OwnerUserId, environment.OwnerMembershipId,
            environment.AdminMembershipId, "grant-correlation");
        var granted = await application.GrantAsync(request);
        Assert.Equal(MembershipPermissionGrantStatus.Granted, granted.Status);
        Assert.NotNull(granted.GrantId);

        var delegatedAdmin = await resolver.ResolveAsync(environment.AdminUserId, environment.OrganisationId);
        Assert.Contains("Organisations.Export", delegatedAdmin.EffectivePermissionCodes);
        Assert.NotEqual(ordinaryAdmin.DecisionVersion, delegatedAdmin.DecisionVersion);

        var replay = await application.GrantAsync(request with { CorrelationId = "grant-replay" });
        Assert.Equal(MembershipPermissionGrantStatus.AlreadyGranted, replay.Status);
        Assert.Equal(granted.GrantId, replay.GrantId);

        var selfGrant = await application.GrantAsync(environment.Request(
            environment.AdminUserId, environment.AdminMembershipId, environment.AdminMembershipId, "self-grant"));
        Assert.Equal(MembershipPermissionGrantStatus.Denied, selfGrant.Status);
        var forgedMembership = await application.GrantAsync(environment.Request(
            environment.OwnerUserId, environment.AdminMembershipId, environment.AdminMembershipId, "forged-membership"));
        Assert.Equal(MembershipPermissionGrantStatus.Denied, forgedMembership.Status);

        var crossOrganisation = await application.GrantAsync(environment.Request(
            environment.OwnerUserId, environment.OwnerMembershipId,
            environment.OtherAdminMembershipId, "cross-organisation"));
        Assert.Equal(MembershipPermissionGrantStatus.Denied, crossOrganisation.Status);
        var viewerGrant = await application.GrantAsync(environment.Request(
            environment.OwnerUserId, environment.OwnerMembershipId,
            environment.ViewerMembershipId, "viewer-grant"));
        Assert.Equal(MembershipPermissionGrantStatus.Denied, viewerGrant.Status);
        var unsupportedPermission = request with
        {
            PermissionKey = "Organisations.Close",
            CorrelationId = "unsupported-permission"
        };
        Assert.Equal(MembershipPermissionGrantStatus.InvalidPermission,
            (await application.GrantAsync(unsupportedPermission)).Status);
        Assert.Equal(MembershipPermissionGrantStatus.Denied,
            (await new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), clock)
                .GrantAsync(unsupportedPermission, default)).Status);

        clock.UtcNow = Now.AddMinutes(2);
        var revoked = await application.RevokeAsync(request with { CorrelationId = "revoke-correlation" });
        Assert.Equal(MembershipPermissionGrantStatus.Revoked, revoked.Status);
        Assert.Equal(granted.GrantId, revoked.GrantId);
        Assert.DoesNotContain("Organisations.Export",
            (await resolver.ResolveAsync(environment.AdminUserId, environment.OrganisationId)).EffectivePermissionCodes);
        Assert.Equal(MembershipPermissionGrantStatus.AlreadyRevoked,
            (await application.RevokeAsync(request with { CorrelationId = "revoke-replay" })).Status);

        await using var verification = new AuthDbContext(Options(environment.AdministratorConnection));
        var grantRecord = await verification.MembershipPermissionGrants.AsNoTracking().SingleAsync();
        Assert.Equal(environment.OwnerMembershipId, grantRecord.GrantedByMembershipId);
        Assert.Equal(environment.OwnerUserId, grantRecord.GrantedBySubjectId);
        Assert.Equal(environment.OwnerMembershipId, grantRecord.RevokedByMembershipId);
        Assert.Equal(environment.OwnerUserId, grantRecord.RevokedBySubjectId);
        Assert.Equal(2, await verification.AuditEntries.CountAsync(entry =>
            entry.SubjectType == nameof(MembershipPermissionGrant)
            && entry.SubjectId == grantRecord.Id.ToString("D")));

        verification.MembershipPermissionGrants.Add(MembershipPermissionGrant.Create(
            Guid.NewGuid(), environment.OrganisationId, environment.AdminMembershipId,
            "Organisations.Close", environment.OwnerMembershipId, environment.OwnerUserId,
            Now.AddMinutes(3)));
        await verification.SaveChangesAsync();
        Assert.Equal(TenantAccessOutcome.Denied,
            (await resolver.ResolveAsync(environment.AdminUserId, environment.OrganisationId)).Outcome);
    }

    [Fact]
    public async Task Grant_survives_grantor_authority_loss_but_former_owner_cannot_mutate_it()
    {
        var environment = await CreateEnvironmentAsync();
        var clock = new ManualTimeProvider(Now.AddMinutes(1));
        var application = new MembershipPermissionGrants(
            new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), clock));
        var request = environment.Request(environment.OwnerUserId, environment.OwnerMembershipId,
            environment.AdminMembershipId, "grant-before-authority-loss");
        Assert.Equal(MembershipPermissionGrantStatus.Granted, (await application.GrantAsync(request)).Status);

        await using (var administrator = new AuthDbContext(Options(environment.AdministratorConnection)))
        {
            var formerOwner = await administrator.OrganisationMemberships.SingleAsync(
                value => value.Id == environment.OwnerMembershipId);
            administrator.Entry(formerOwner).Property(value => value.PermissionSetId).CurrentValue =
                PermissionSet.OrganisationAdministratorId;
            await administrator.SaveChangesAsync();
        }

        await using var database = new AuthDbContext(Options(environment.RuntimeConnection));
        var delegated = await new CurrentTenantAccessResolver(database, clock)
            .ResolveAsync(environment.AdminUserId, environment.OrganisationId);
        Assert.Contains("Organisations.Export", delegated.EffectivePermissionCodes);
        Assert.Equal(MembershipPermissionGrantStatus.Denied,
            (await application.RevokeAsync(request with { CorrelationId = "former-owner-revoke" })).Status);
        await using var verification = new AuthDbContext(Options(environment.AdministratorConnection));
        Assert.Null((await verification.MembershipPermissionGrants.SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task Concurrent_duplicate_grants_have_one_durable_effect_and_return_one_identity()
    {
        var environment = await CreateEnvironmentAsync();
        var clock = new ManualTimeProvider(Now.AddMinutes(1));
        var request = environment.Request(environment.OwnerUserId, environment.OwnerMembershipId,
            environment.AdminMembershipId, "concurrent-grant");
        var ready = new CountdownEvent(2);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<MembershipPermissionGrantResult> ExecuteAsync()
        {
            var application = new MembershipPermissionGrants(
                new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), clock));
            ready.Signal();
            await start.Task;
            return await application.GrantAsync(request);
        }

        var first = ExecuteAsync();
        var second = ExecuteAsync();
        ready.Wait();
        start.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Contains(results, result => result.Status == MembershipPermissionGrantStatus.Granted);
        Assert.Contains(results, result => result.Status == MembershipPermissionGrantStatus.AlreadyGranted);
        Assert.Single(results.Select(result => result.GrantId).Distinct());
        await using var verification = new AuthDbContext(Options(environment.AdministratorConnection));
        Assert.Equal(1, await verification.MembershipPermissionGrants.CountAsync());
        Assert.Equal(1, await verification.AuditEntries.CountAsync(entry =>
            entry.Action == "MembershipPermission.Granted"));
    }

    [Fact]
    public async Task Inactive_target_is_ignored_but_history_survives_and_current_owner_can_revoke()
    {
        var environment = await CreateEnvironmentAsync();
        var clock = new ManualTimeProvider(Now.AddMinutes(1));
        var application = new MembershipPermissionGrants(
            new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), clock));
        var request = environment.Request(environment.OwnerUserId, environment.OwnerMembershipId,
            environment.AdminMembershipId, "grant-before-suspension");
        Assert.Equal(MembershipPermissionGrantStatus.Granted, (await application.GrantAsync(request)).Status);

        await using (var administrator = new AuthDbContext(Options(environment.AdministratorConnection)))
        {
            var membership = await administrator.OrganisationMemberships.SingleAsync(
                value => value.Id == environment.AdminMembershipId);
            Assert.True(membership.Suspend(Now.AddMinutes(2)));
            await administrator.SaveChangesAsync();
        }

        await using var database = new AuthDbContext(Options(environment.RuntimeConnection));
        var resolver = new CurrentTenantAccessResolver(database, clock);
        Assert.Equal(TenantAccessOutcome.Denied,
            (await resolver.ResolveAsync(environment.AdminUserId, environment.OrganisationId)).Outcome);
        await using var verification = new AuthDbContext(Options(environment.AdministratorConnection));
        Assert.Equal(1, await verification.MembershipPermissionGrants.CountAsync());
        Assert.Equal(MembershipPermissionGrantStatus.Revoked,
            (await application.RevokeAsync(request with { CorrelationId = "revoke-inactive-target" })).Status);
        Assert.Equal(1, await verification.MembershipPermissionGrants.CountAsync());
        Assert.Equal(MembershipPermissionGrantStatus.Denied,
            (await application.GrantAsync(request with { CorrelationId = "regrant-inactive-target" })).Status);
    }

    [Fact]
    public async Task Runtime_rls_rejects_cross_organisation_permission_grant_manipulation()
    {
        var environment = await CreateEnvironmentAsync();
        await using var connection = new NpgsqlConnection(environment.RuntimeConnection);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var setTenant = new NpgsqlCommand(
            "select pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            setTenant.Parameters.AddWithValue("organisation", environment.OrganisationId.ToString("D"));
            await setTenant.ExecuteScalarAsync();
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO public."MembershipPermissionGrants"
              ("Id", "OrganisationId", "OrganisationMembershipId", "PermissionKey",
               "GrantedByMembershipId", "GrantedBySubjectId", "GrantedAtUtc", "ConcurrencyVersion")
            VALUES (@id, @organisation, @target, 'Organisations.Export', @owner, @subject, @at, 1)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organisation", environment.OtherOrganisationId);
        command.Parameters.AddWithValue("target", environment.OtherAdminMembershipId);
        command.Parameters.AddWithValue("owner", environment.OtherOwnerMembershipId);
        command.Parameters.AddWithValue("subject", environment.OtherOwnerUserId);
        command.Parameters.AddWithValue("at", Now);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private async Task<Environment> CreateEnvironmentAsync()
    {
        var administratorConnection = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(administratorConnection, ReadBootstrapScript());
        await ExecuteAsync(administratorConnection,
            $"ALTER ROLE zeka_auth_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}';");
        var migratorConnection = Connection(administratorConnection, "zeka_auth_migrator", MigratorPassword);
        var runtimeConnection = Connection(administratorConnection, "zeka_auth_runtime", RuntimePassword);

        await using (var migration = new AuthDbContext(Options(migratorConnection)))
        {
            await migration.Database.OpenConnectionAsync();
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await migration.GetService<IMigrator>().MigrateAsync();
        }
        await ExecuteAsync(administratorConnection, ReadBootstrapScript());

        var owner = User.Create("owner@example.invalid", "owner", "Synthetic", "Owner", Now);
        var admin = User.Create("admin@example.invalid", "admin", "Synthetic", "Admin", Now);
        var viewer = User.Create("viewer@example.invalid", "viewer", "Synthetic", "Viewer", Now);
        var otherOwner = User.Create("other-owner@example.invalid", "other-owner", "Other", "Owner", Now);
        var otherAdmin = User.Create("other-admin@example.invalid", "other-admin", "Other", "Admin", Now);
        foreach (var user in new[] { owner, admin, viewer, otherOwner, otherAdmin })
        {
            user.EmailConfirmed = true;
        }
        var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic One", owner.Id, Now);
        var otherOrganisation = Organisation.Create(Guid.NewGuid(), "Synthetic Two", otherOwner.Id, Now);
        Assert.True(organisation.Activate(Now));
        Assert.True(otherOrganisation.Activate(Now));
        var ownerMembership = OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, owner.Id, Now);
        var adminMembership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, admin.Id,
            PermissionSet.OrganisationAdministratorId, Now);
        var viewerMembership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, viewer.Id,
            PermissionSet.ViewerId, Now);
        var otherOwnerMembership = OrganisationMembership.CreateOwner(
            Guid.NewGuid(), otherOrganisation.Id, otherOwner.Id, Now);
        var otherAdminMembership = OrganisationMembership.Create(Guid.NewGuid(), otherOrganisation.Id, otherAdmin.Id,
            PermissionSet.OrganisationAdministratorId, Now);

        await using (var database = new AuthDbContext(Options(runtimeConnection)))
        {
            database.AddRange(owner, admin, viewer, otherOwner, otherAdmin, organisation, otherOrganisation,
                ownerMembership, adminMembership, viewerMembership, otherOwnerMembership, otherAdminMembership);
            foreach (var user in new[] { owner, admin, viewer, otherOwner, otherAdmin })
            {
                database.Entry(user).Property(value => value.Status).CurrentValue = UserStatus.Active;
            }
            await database.SaveChangesAsync();
        }

        return new Environment(
            administratorConnection,
            runtimeConnection,
            organisation.Id,
            owner.Id,
            ownerMembership.Id,
            admin.Id,
            adminMembership.Id,
            viewerMembership.Id,
            otherOrganisation.Id,
            otherOwner.Id,
            otherOwnerMembership.Id,
            otherAdminMembership.Id);
    }

    private static DbContextOptions<AuthDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).Options;

    private static string Connection(string administratorConnection, string username, string password) =>
        new NpgsqlConnectionStringBuilder(administratorConnection)
        {
            Username = username,
            Password = password,
            Pooling = false
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadBootstrapScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }

        throw new FileNotFoundException("The reviewed AuthManagement role bootstrap was not found.");
    }

    private sealed record Environment(
        string AdministratorConnection,
        string RuntimeConnection,
        Guid OrganisationId,
        Guid OwnerUserId,
        Guid OwnerMembershipId,
        Guid AdminUserId,
        Guid AdminMembershipId,
        Guid ViewerMembershipId,
        Guid OtherOrganisationId,
        Guid OtherOwnerUserId,
        Guid OtherOwnerMembershipId,
        Guid OtherAdminMembershipId)
    {
        public MembershipPermissionGrantRequest Request(
            Guid actorSubjectId,
            Guid actorMembershipId,
            Guid targetMembershipId,
            string correlationId) =>
            new(actorSubjectId, actorMembershipId, OrganisationId, targetMembershipId,
                "Organisations.Export", correlationId);
    }
}
