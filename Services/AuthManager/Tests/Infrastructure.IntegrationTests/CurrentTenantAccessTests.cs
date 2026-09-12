using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

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
}
