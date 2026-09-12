using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

public sealed class OrganisationPersistenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Fixed_system_permission_sets_are_seeded()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var permissionSets = await dbContext.PermissionSets.OrderBy(value => value.Code).ToListAsync();

        permissionSets.Should().HaveCount(7);
        permissionSets.Should().OnlyContain(value => value.IsSystem);
        permissionSets.Select(value => value.Code).Should().BeEquivalentTo(
            "Owner", "Admin", "LimitedViewer", "LimitedEditor", "Viewer", "Contributor", "Editor");
    }

    [Fact]
    public async Task Duplicate_display_names_are_allowed()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var firstOwner = CreateUser("first@example.org", "first-owner");
        var secondOwner = CreateUser("second@example.org", "second-owner");
        dbContext.AddRange(firstOwner, secondOwner);
        dbContext.Organisations.AddRange(
            Organisation.Create(Guid.NewGuid(), "Shared name", firstOwner.Id, Now),
            Organisation.Create(Guid.NewGuid(), "Shared name", secondOwner.Id, Now));

        var action = () => dbContext.SaveChangesAsync();

        await action.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Duplicate_membership_for_same_user_and_organisation_is_rejected()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var owner = CreateUser("owner@example.org", "owner");
        var organisation = Organisation.Create(Guid.NewGuid(), "Example", owner.Id, Now);
        dbContext.AddRange(owner, organisation);
        dbContext.OrganisationMemberships.AddRange(
            OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, owner.Id, Now),
            OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, owner.Id,
                PermissionSet.ViewerId, Now));

        var action = () => dbContext.SaveChangesAsync();

        await action.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task One_user_can_have_memberships_in_multiple_organisations()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        await using var scope = context.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var owner = CreateUser("owner@example.org", "owner");
        var first = Organisation.Create(Guid.NewGuid(), "First", owner.Id, Now);
        var second = Organisation.Create(Guid.NewGuid(), "Second", owner.Id, Now);
        dbContext.AddRange(owner, first, second);
        dbContext.OrganisationMemberships.AddRange(
            OrganisationMembership.CreateOwner(Guid.NewGuid(), first.Id, owner.Id, Now),
            OrganisationMembership.CreateOwner(Guid.NewGuid(), second.Id, owner.Id, Now));

        await dbContext.SaveChangesAsync();

        (await dbContext.OrganisationMemberships.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_membership_updates_are_rejected()
    {
        await using var context = await IdentityTestContext.CreateAsync();
        var membershipId = Guid.NewGuid();

        await using (var arrangeScope = context.Services.CreateAsyncScope())
        {
            var dbContext = arrangeScope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var owner = CreateUser("owner@example.org", "owner");
            var organisation = Organisation.Create(Guid.NewGuid(), "Example", owner.Id, Now);
            dbContext.AddRange(owner, organisation);
            dbContext.OrganisationMemberships.Add(
                OrganisationMembership.CreateOwner(membershipId, organisation.Id, owner.Id, Now));
            await dbContext.SaveChangesAsync();
        }

        await using var firstScope = context.Services.CreateAsyncScope();
        await using var secondScope = context.Services.CreateAsyncScope();
        var firstDbContext = firstScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var secondDbContext = secondScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var first = await firstDbContext.OrganisationMemberships.SingleAsync(value => value.Id == membershipId);
        var second = await secondDbContext.OrganisationMemberships.SingleAsync(value => value.Id == membershipId);
        first.Suspend(Now.AddMinutes(1)).Should().BeTrue();
        second.Suspend(Now.AddMinutes(2)).Should().BeTrue();
        await firstDbContext.SaveChangesAsync();

        var action = () => secondDbContext.SaveChangesAsync();

        await action.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public void Application_user_remains_tenant_neutral()
    {
        var userProperties = typeof(User).GetProperties().Select(property => property.Name).ToArray();

        userProperties.Should().NotContain("OrganisationId");
        userProperties.Should().NotContain("TenantId");
        userProperties.Should().NotContain("PermissionSetId");
    }

    private static User CreateUser(string email, string userName) =>
        User.Create(email, userName, "Ada", "Lovelace", Now);
}
