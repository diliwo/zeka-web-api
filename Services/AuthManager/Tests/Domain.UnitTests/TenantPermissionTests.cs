using AuthManager.Core.Organisations;
using FluentAssertions;

namespace Domain.UnitTests;

public sealed class TenantPermissionTests
{
    [Theory]
    [InlineData("LimitedViewer", "Clients.ViewAssigned,ReferenceData.View")]
    [InlineData("LimitedEditor", "Clients.ViewAssigned,Clients.Create,Clients.EditAssigned,ReferenceData.View")]
    [InlineData("Viewer", "Clients.ViewAssigned,Clients.ViewAll,TeamConfiguration.View,Partners.View,PartnerDocuments.View,ReferenceData.View")]
    [InlineData("Contributor", "Clients.ViewAssigned,Clients.ViewAll,Clients.Create,Clients.EditAssigned,Clients.ImportExport,TeamConfiguration.View,Partners.View,Partners.Manage,PartnerDocuments.View,PartnerDocuments.Import,ReferenceData.View")]
    [InlineData("Editor", "Clients.ViewAssigned,Clients.ViewAll,Clients.Create,Clients.EditAssigned,Clients.EditAll,Clients.ImportExport,TeamConfiguration.View,Partners.View,Partners.Manage,PartnerDocuments.View,PartnerDocuments.Import,ReferenceData.View")]
    [InlineData("Admin", "Clients.ViewAssigned,Clients.ViewAll,Clients.Create,Clients.EditAssigned,Clients.EditAll,Clients.Delete,Clients.ImportExport,TeamConfiguration.View,TeamConfiguration.ManageTeams,TeamConfiguration.ManageStaffProfiles,TeamConfiguration.ManageMemberships,Partners.View,Partners.Manage,Partners.Delete,PartnerDocuments.View,PartnerDocuments.Import,PartnerDocuments.Delete,ReferenceData.View")]
    [InlineData("Owner", "Clients.ViewAssigned,Clients.ViewAll,Clients.Create,Clients.EditAssigned,Clients.EditAll,Clients.Delete,Clients.ImportExport,TeamConfiguration.View,TeamConfiguration.ManageTeams,TeamConfiguration.ManageStaffProfiles,TeamConfiguration.ManageMemberships,Partners.View,Partners.Manage,Partners.Delete,PartnerDocuments.View,PartnerDocuments.Import,PartnerDocuments.Delete,ReferenceData.View")]
    public void Grants_match_the_accepted_product_matrix_exactly(string role, string expected)
        => TenantPermissions.Resolve(role).Should().BeEquivalentTo(expected.Split(','));

    [Theory]
    [InlineData("Member")]
    [InlineData("OrganisationOwner")]
    [InlineData("owner")]
    [InlineData("PlatformAdministrator")]
    [InlineData("")]
    public void Unknown_and_unmigrated_roles_fail_closed(string role)
        => TenantPermissions.Resolve(role).Should().BeNull();

    [Fact]
    public void Tenant_roles_never_grant_platform_reference_mutation()
    {
        TenantPermissions.IsKnown("Platform.ReferenceData.Manage").Should().BeFalse();
        TenantPermissions.IsKnown("Clients.Unknown").Should().BeFalse();
        PermissionSet.SystemPermissionSets.Should().HaveCount(7);
        foreach (var role in PermissionSet.SystemPermissionSets)
            TenantPermissions.Resolve(role.Code).Should().NotContain("Platform.ReferenceData.Manage");
    }
}
