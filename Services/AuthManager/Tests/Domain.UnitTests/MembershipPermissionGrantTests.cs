using AuthManager.Core.Organisations;
using FluentAssertions;

namespace Domain.UnitTests;

public sealed class MembershipPermissionGrantTests
{
    private static readonly DateTimeOffset GrantedAt =
        new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Grant_preserves_authority_identity_and_revocation_preserves_history()
    {
        var id = Guid.NewGuid();
        var organisation = Guid.NewGuid();
        var target = Guid.NewGuid();
        var ownerMembership = Guid.NewGuid();
        var ownerSubject = Guid.NewGuid();
        var revokerMembership = Guid.NewGuid();
        var revokerSubject = Guid.NewGuid();
        var grant = MembershipPermissionGrant.Create(
            id,
            organisation,
            target,
            "Organisations.Export",
            ownerMembership,
            ownerSubject,
            GrantedAt);

        grant.IsActive.Should().BeTrue();
        grant.ConcurrencyVersion.Should().Be(1);

        grant.Revoke(revokerMembership, revokerSubject, GrantedAt.AddMinutes(1)).Should().BeTrue();

        grant.IsActive.Should().BeFalse();
        grant.GrantedByMembershipId.Should().Be(ownerMembership);
        grant.GrantedBySubjectId.Should().Be(ownerSubject);
        grant.GrantedAtUtc.Should().Be(GrantedAt);
        grant.RevokedByMembershipId.Should().Be(revokerMembership);
        grant.RevokedBySubjectId.Should().Be(revokerSubject);
        grant.RevokedAtUtc.Should().Be(GrantedAt.AddMinutes(1));
        grant.ConcurrencyVersion.Should().Be(2);
    }

    [Fact]
    public void Repeated_or_backdated_revoke_is_a_no_op()
    {
        var grant = MembershipPermissionGrant.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Organisations.Export",
            Guid.NewGuid(), Guid.NewGuid(), GrantedAt);

        grant.Revoke(Guid.NewGuid(), Guid.NewGuid(), GrantedAt.AddSeconds(-1)).Should().BeFalse();
        grant.IsActive.Should().BeTrue();
        grant.Revoke(Guid.NewGuid(), Guid.NewGuid(), GrantedAt).Should().BeTrue();
        grant.Revoke(Guid.NewGuid(), Guid.NewGuid(), GrantedAt.AddMinutes(1)).Should().BeFalse();
        grant.ConcurrencyVersion.Should().Be(2);
    }
}
