using AuthManager.Core.Organisations;
using FluentAssertions;

namespace Domain.UnitTests;

public sealed class OrganisationMembershipTests
{
    private static readonly DateTimeOffset JoinedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Owner_membership_is_active_and_uses_owner_permission_set()
    {
        var organisationId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();

        var membership = OrganisationMembership.CreateOwner(
            Guid.NewGuid(), organisationId, ownerUserId, JoinedAt);

        membership.OrganisationId.Should().Be(organisationId);
        membership.UserId.Should().Be(ownerUserId);
        membership.PermissionSetId.Should().Be(PermissionSet.OrganisationOwnerId);
        membership.Status.Should().Be(MembershipStatus.Active);
        membership.ConcurrencyVersion.Should().Be(1);
    }

    [Fact]
    public void Membership_lifecycle_tracks_suspension_reactivation_and_departure()
    {
        var membership = CreateMembership();
        var suspendedAt = JoinedAt.AddMinutes(1);

        membership.Suspend(suspendedAt).Should().BeTrue();
        membership.SuspendedAtUtc.Should().Be(suspendedAt);
        membership.Reactivate(JoinedAt.AddMinutes(2)).Should().BeTrue();
        membership.SuspendedAtUtc.Should().BeNull();
        membership.Leave(JoinedAt.AddMinutes(3)).Should().BeTrue();

        membership.Status.Should().Be(MembershipStatus.Left);
        membership.EndedAtUtc.Should().Be(JoinedAt.AddMinutes(3));
        membership.ConcurrencyVersion.Should().Be(4);
    }

    [Fact]
    public void Left_membership_is_terminal()
    {
        var membership = CreateMembership();
        membership.Leave(JoinedAt.AddMinutes(1)).Should().BeTrue();

        membership.Suspend(JoinedAt.AddMinutes(2)).Should().BeFalse();
        membership.Reactivate(JoinedAt.AddMinutes(2)).Should().BeFalse();
        membership.Leave(JoinedAt.AddMinutes(2)).Should().BeFalse();
    }

    [Fact]
    public void System_permission_sets_have_stable_distinct_codes_and_identifiers()
    {
        PermissionSet.SystemPermissionSets.Select(value => value.Code)
            .Should().Equal("OrganisationOwner", "OrganisationAdministrator", "Member");
        PermissionSet.SystemPermissionSets.Select(value => value.Id)
            .Should().OnlyHaveUniqueItems();
        PermissionSet.SystemPermissionSets.Should().OnlyContain(value => value.IsSystem);
    }

    private static OrganisationMembership CreateMembership() =>
        OrganisationMembership.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), PermissionSet.MemberId, JoinedAt);
}
