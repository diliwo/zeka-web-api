using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class CurrentTenantAccessTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Theory]
    [InlineData("membership")] [InlineData("organisation")] [InlineData("user")]
    [InlineData("lockout")] [InlineData("confirmation")] [InlineData("permission")]
    public async Task Each_call_observes_current_authoritative_state_and_never_reuses_a_positive_decision(string revoked)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var database = new AuthDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var user = User.Create("synthetic@example.invalid", "synthetic", "Test", "User", now);
        user.EmailConfirmed = true;
        database.Add(user);
        database.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Active;
        var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic", user.Id, now);
        organisation.Activate(now);
        var membership = OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, user.Id, now);
        database.AddRange(organisation, membership); await database.SaveChangesAsync();
        var resolver = new CurrentTenantAccessResolver(database, TimeProvider.System);
        var links = new ActiveOrganisationMembership(database);
        Assert.True(await links.ExistsAsync(organisation.Id, membership.Id, default));
        Assert.False(await links.ExistsAsync(Guid.NewGuid(), membership.Id, default, requireActive: false));
        var first = await resolver.ResolveAsync(user.Id, organisation.Id);
        Assert.Equal(TenantAccessOutcome.Authorized, first.Outcome);
        Assert.Contains("Clients.Delete", first.EffectivePermissionCodes);
        Assert.Equal(TenantAccessOutcome.Denied, (await resolver.ResolveAsync(Guid.NewGuid(), organisation.Id)).Outcome);
        Assert.Equal(TenantAccessOutcome.Denied, (await resolver.ResolveAsync(user.Id, Guid.NewGuid())).Outcome);
        switch (revoked)
        {
            case "membership": membership.Suspend(now.AddSeconds(1)); break;
            case "organisation": organisation.Suspend(now.AddSeconds(1)); break;
            case "user": database.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.PendingVerification; break;
            case "lockout": user.LockoutEnd = now.AddHours(1); break;
            case "confirmation": user.EmailConfirmed = false; break;
            case "permission": database.Entry(membership).Property(x => x.PermissionSetId).CurrentValue = PermissionSet.LimitedViewerId; break;
        }
        await database.SaveChangesAsync();
        var current = await resolver.ResolveAsync(user.Id, organisation.Id);
        if (revoked == "membership")
        {
            Assert.False(await links.ExistsAsync(organisation.Id, membership.Id, default));
            Assert.True(await links.ExistsAsync(organisation.Id, membership.Id, default, requireActive: false));
        }
        if (revoked == "permission")
        {
            Assert.Equal(TenantAccessOutcome.Authorized, current.Outcome);
            Assert.DoesNotContain("Clients.Delete", current.EffectivePermissionCodes);
            Assert.Contains("Clients.ViewAssigned", current.EffectivePermissionCodes);
        }
        else Assert.Equal(TenantAccessOutcome.Denied, current.Outcome);
    }

    [Fact]
    [Trait("Issue", "ZEK-17")]
    public async Task Distinct_workers_resolve_the_exact_singleton_profile_without_administrator_permissions()
    {
        var environment = await CreateWorkerEnvironmentAsync();
        await using var database = new AuthDbContext(environment.Options);
        var resolver = new CurrentTenantAccessResolver(database, TimeProvider.System);

        var retry = await resolver.ResolveAsync(environment.RetryUserId, environment.OrganisationId);
        var consumer = await resolver.ResolveAsync(environment.ConsumerUserId, environment.OrganisationId);

        Assert.Equal(TenantAccessOutcome.Authorized, retry.Outcome);
        Assert.Equal(TenantAccessOutcome.Authorized, consumer.Outcome);
        Assert.NotEqual(environment.RetryUserId, environment.ConsumerUserId);
        Assert.NotEqual(retry.OrganisationMembershipId, consumer.OrganisationMembershipId);
        Assert.Equal(new[] { "TeamConfiguration.ManageStaffProfiles" }, retry.EffectivePermissionCodes);
        Assert.Equal(new[] { "TeamConfiguration.ManageStaffProfiles" }, consumer.EffectivePermissionCodes);
        Assert.Equal(TenantAccessOutcome.Denied,
            (await resolver.ResolveAsync(environment.RetryUserId, Guid.NewGuid())).Outcome);
        var owner = await resolver.ResolveAsync(environment.OwnerUserId, environment.OrganisationId);
        Assert.Equal(TenantAccessOutcome.Authorized, owner.Outcome);
        Assert.Contains("Clients.Delete", owner.EffectivePermissionCodes);
        Assert.Contains("Organisations.Export", owner.EffectivePermissionCodes);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("code")]
    [InlineData("system")]
    [InlineData("membership")]
    [Trait("Issue", "ZEK-17")]
    public async Task Worker_authority_is_denied_after_catalogue_forgery_or_membership_suspension(string changed)
    {
        var environment = await CreateWorkerEnvironmentAsync();
        await using var database = new AuthDbContext(environment.Options);
        var resolver = new CurrentTenantAccessResolver(database, TimeProvider.System);
        Assert.Equal(TenantAccessOutcome.Authorized,
            (await resolver.ResolveAsync(environment.RetryUserId, environment.OrganisationId)).Outcome);
        var profile = await database.PermissionSets.SingleAsync(value => value.Id == PermissionSet.StaffProjectionWorkerId);
        var membership = await database.OrganisationMemberships.SingleAsync(value => value.Id == environment.RetryMembershipId);
        switch (changed)
        {
            case "identity":
                database.Entry(profile).Property(value => value.Code).CurrentValue = "UnrecognizedWorkerProfile";
                await database.SaveChangesAsync();
                var forged = PermissionSet.CreateStaffProjectionWorker();
                database.Entry(forged).Property(value => value.Id).CurrentValue = Guid.NewGuid();
                database.Add(forged);
                database.Entry(membership).Property(value => value.PermissionSetId).CurrentValue = forged.Id;
                break;
            case "code":
                var administrator = await database.PermissionSets.SingleAsync(value => value.Id == PermissionSet.OrganisationAdministratorId);
                database.Entry(administrator).Property(value => value.Code).CurrentValue = "UnrecognizedAdminProfile";
                // Release the unique code in this disposable corruption fixture.
                await database.SaveChangesAsync();
                database.Entry(profile).Property(value => value.Code).CurrentValue = "Admin";
                break;
            case "system": database.Entry(profile).Property(value => value.IsSystem).CurrentValue = false; break;
            case "membership": Assert.True(membership.Suspend(DateTimeOffset.UtcNow)); break;
        }
        await database.SaveChangesAsync();

        var result = await resolver.ResolveAsync(environment.RetryUserId, environment.OrganisationId);

        Assert.Equal(TenantAccessOutcome.Denied, result.Outcome);
        Assert.Empty(result.EffectivePermissionCodes);
    }

    [Theory]
    [InlineData("Organisations.Export")]
    [InlineData("TeamConfiguration.ManageStaffProfiles")]
    [InlineData("Clients.Unknown")]
    [Trait("Issue", "ZEK-17")]
    public async Task Unsupported_active_grant_denies_the_entire_worker_authority_observation(string permission)
    {
        var environment = await CreateWorkerEnvironmentAsync();
        await using var database = new AuthDbContext(environment.Options);
        var resolver = new CurrentTenantAccessResolver(database, TimeProvider.System);
        Assert.Equal(TenantAccessOutcome.Authorized,
            (await resolver.ResolveAsync(environment.RetryUserId, environment.OrganisationId)).Outcome);
        database.MembershipPermissionGrants.Add(MembershipPermissionGrant.Create(Guid.NewGuid(),
            environment.OrganisationId, environment.RetryMembershipId, permission,
            environment.OwnerMembershipId, environment.OwnerUserId, DateTimeOffset.UtcNow));
        await database.SaveChangesAsync();

        var result = await resolver.ResolveAsync(environment.RetryUserId, environment.OrganisationId);

        Assert.Equal(TenantAccessOutcome.Denied, result.Outcome);
        Assert.Empty(result.EffectivePermissionCodes);
    }

    private async Task<WorkerEnvironment> CreateWorkerEnvironmentAsync()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
        await using var database = new AuthDbContext(options);
        await database.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var owner = User.Create("owner@example.invalid", "owner", "Synthetic", "Owner", now);
        var retry = User.Create("retry@example.invalid", "retry", "Synthetic", "Retry", now);
        var consumer = User.Create("consumer@example.invalid", "consumer", "Synthetic", "Consumer", now);
        // Test-only eligibility setup follows the existing fixture. This is not an
        // implementation of user verification, activation or worker enrollment.
        foreach (var user in new[] { owner, retry, consumer })
        {
            user.EmailConfirmed = true;
            database.Add(user);
            database.Entry(user).Property(value => value.Status).CurrentValue = UserStatus.Active;
        }
        var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic Workers", owner.Id, now);
        Assert.True(organisation.Activate(now));
        var ownerMembership = OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, owner.Id, now);
        var retryMembership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, retry.Id,
            PermissionSet.StaffProjectionWorkerId, now);
        var consumerMembership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, consumer.Id,
            PermissionSet.StaffProjectionWorkerId, now);
        database.AddRange(PermissionSet.CreateStaffProjectionWorker(), organisation,
            ownerMembership, retryMembership, consumerMembership);
        await database.SaveChangesAsync();
        return new WorkerEnvironment(options, organisation.Id, owner.Id, ownerMembership.Id,
            retry.Id, retryMembership.Id, consumer.Id);
    }

    private sealed record WorkerEnvironment(DbContextOptions<AuthDbContext> Options, Guid OrganisationId,
        Guid OwnerUserId, Guid OwnerMembershipId, Guid RetryUserId, Guid RetryMembershipId, Guid ConsumerUserId);
}
