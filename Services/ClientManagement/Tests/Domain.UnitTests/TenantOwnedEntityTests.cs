using ClientManagement.Core.Entities;

namespace Domain.UnitTests;

public sealed class TenantOwnedEntityTests
{
    [Fact]
    public void Organisation_id_cannot_be_changed_after_assignment()
    {
        var worker = new SocialWorker("Ada", "Lovelace", "Support", "SUP", "ada");
        worker.AssignToOrganisation(Guid.NewGuid());

        var action = () => worker.AssignToOrganisation(Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(action);
    }
}
