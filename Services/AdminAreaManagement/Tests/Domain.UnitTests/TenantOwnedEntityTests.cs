using AdminAreaManagement.Core.Entities;
using Xunit;

namespace Domain.UnitTests;

public sealed class TenantOwnedEntityTests
{
    [Fact]
    public void Organisation_id_cannot_be_changed_after_assignment()
    {
        var team = new Team("Support", "SUP");
        team.AssignToOrganisation(Guid.NewGuid());

        var action = () => team.AssignToOrganisation(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(action);
    }
}
