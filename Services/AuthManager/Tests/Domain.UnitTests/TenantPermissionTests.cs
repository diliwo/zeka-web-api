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
    [InlineData("Owner", "Clients.ViewAssigned,Clients.ViewAll,Clients.Create,Clients.EditAssigned,Clients.EditAll,Clients.Delete,Clients.ImportExport,TeamConfiguration.View,TeamConfiguration.ManageTeams,TeamConfiguration.ManageStaffProfiles,TeamConfiguration.ManageMemberships,Partners.View,Partners.Manage,Partners.Delete,PartnerDocuments.View,PartnerDocuments.Import,PartnerDocuments.Delete,ReferenceData.View,Organisations.Export,Organisations.Close")]
    public void Grants_match_the_accepted_product_matrix_exactly(string role, string expected)
    {
        TenantPermissions.Resolve(role).Should().BeEquivalentTo(expected.Split(','));
        var profile = PermissionSet.SystemPermissionSets.Single(value => value.Code == role);
        TenantPermissions.Resolve(profile.Id, role).Should().BeEquivalentTo(expected.Split(','));
    }

    [Theory]
    [InlineData("Member")]
    [InlineData("OrganisationOwner")]
    [InlineData("owner")]
    [InlineData("PlatformAdministrator")]
    [InlineData("")]
    public void Unknown_and_unmigrated_roles_fail_closed(string role)
        => TenantPermissions.Resolve(role).Should().BeNull();

    [Fact]
    public void Approved_worker_profile_has_one_permission_and_does_not_change_human_migration_seeds()
    {
        var profile = PermissionSet.CreateStaffProjectionWorker();
        profile.Id.Should().Be(PermissionSet.StaffProjectionWorkerId);
        profile.Code.Should().Be(PermissionSet.StaffProjectionWorkerCode);
        profile.IsSystem.Should().BeTrue();
        PermissionSet.SystemPermissionSets.Should().NotContain(value => value.Id == profile.Id || value.Code == profile.Code);
        TenantPermissions.Resolve(profile.Id, profile.Code).Should()
            .BeEquivalentTo(new[] { "TeamConfiguration.ManageStaffProfiles" });
        TenantPermissions.Resolve(profile.Code).Should()
            .BeEquivalentTo(new[] { "TeamConfiguration.ManageStaffProfiles" });
        PermissionSet.CreateStaffProjectionWorker().Should().NotBeSameAs(profile);
    }

    [Fact]
    public void Worker_code_on_a_different_permission_set_identity_fails_closed()
        => TenantPermissions.Resolve(Guid.NewGuid(), PermissionSet.StaffProjectionWorkerCode).Should().BeNull();

    [Theory]
    [InlineData("Admin")]
    [InlineData("Owner")]
    [InlineData("staffprojectionworker")]
    [InlineData("Member")]
    public void Worker_identity_with_a_different_code_fails_closed(string code)
        => TenantPermissions.Resolve(PermissionSet.StaffProjectionWorkerId, code).Should().BeNull();

    [Theory]
    [InlineData("TeamConfiguration.ManageStaffProfiles")]
    [InlineData("Organisations.Export")]
    [InlineData("Organisations.Close")]
    [InlineData("Clients.Delete")]
    [InlineData("ReferenceData.View")]
    public void Worker_profile_cannot_receive_explicit_grants(string permission)
        => TenantPermissions.IsExplicitGrantAllowed(PermissionSet.StaffProjectionWorkerCode, permission).Should().BeFalse();

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
