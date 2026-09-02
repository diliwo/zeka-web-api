using AuthManager.Core.Organisations;
using FluentAssertions;

namespace Domain.UnitTests;

public sealed class OrganisationTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_establishes_immutable_identity_owner_and_pending_lifecycle()
    {
        var id = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();

        var organisation = Organisation.Create(id, "  Example NGO  ", ownerUserId, CreatedAt);

        organisation.Id.Should().Be(id);
        organisation.OwnerUserId.Should().Be(ownerUserId);
        organisation.DisplayName.Should().Be("Example NGO");
        organisation.Status.Should().Be(OrganisationStatus.Pending);
        organisation.CreatedAtUtc.Should().Be(CreatedAt);
        organisation.UpdatedAtUtc.Should().Be(CreatedAt);
        organisation.ConcurrencyVersion.Should().Be(1);
        typeof(Organisation).GetProperty(nameof(Organisation.Id))!.SetMethod!.IsPrivate.Should().BeTrue();
        typeof(Organisation).GetProperty(nameof(Organisation.OwnerUserId))!.SetMethod!.IsPrivate.Should().BeTrue();
    }

    [Fact]
    public void Lifecycle_allows_activation_suspension_reactivation_and_closure()
    {
        var organisation = CreateOrganisation();

        organisation.Activate(CreatedAt.AddMinutes(1)).Should().BeTrue();
        organisation.Suspend(CreatedAt.AddMinutes(2)).Should().BeTrue();
        organisation.Activate(CreatedAt.AddMinutes(3)).Should().BeTrue();
        organisation.Close(CreatedAt.AddMinutes(4)).Should().BeTrue();

        organisation.Status.Should().Be(OrganisationStatus.Closed);
        organisation.ConcurrencyVersion.Should().Be(5);
    }

    [Fact]
    public void Invalid_or_stale_lifecycle_transition_is_rejected_without_mutation()
    {
        var organisation = CreateOrganisation();

        organisation.Suspend(CreatedAt.AddMinutes(1)).Should().BeFalse();
        organisation.Activate(CreatedAt.AddMinutes(1)).Should().BeTrue();
        organisation.Suspend(CreatedAt).Should().BeFalse();

        organisation.Status.Should().Be(OrganisationStatus.Active);
        organisation.ConcurrencyVersion.Should().Be(2);
    }

    [Fact]
    public void Closed_organisation_is_terminal()
    {
        var organisation = CreateOrganisation();
        organisation.Close(CreatedAt.AddMinutes(1)).Should().BeTrue();

        organisation.Activate(CreatedAt.AddMinutes(2)).Should().BeFalse();
        organisation.Suspend(CreatedAt.AddMinutes(2)).Should().BeFalse();

        organisation.Status.Should().Be(OrganisationStatus.Closed);
    }

    private static Organisation CreateOrganisation() =>
        Organisation.Create(Guid.NewGuid(), "Example NGO", Guid.NewGuid(), CreatedAt);
}
