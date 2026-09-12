using System.Collections.Frozen;

namespace AuthManager.Core.Organisations;

/// <summary>Version 1 of the accepted, centrally owned tenant permission catalogue.</summary>
public static class TenantPermissions
{
    private static readonly string[] View = ["Clients.ViewAssigned", "Clients.ViewAll"];
    private static readonly string[] AdministrationView =
        ["TeamConfiguration.View", "Partners.View", "PartnerDocuments.View"];
    private static readonly string[] Contribution = ["Partners.Manage", "PartnerDocuments.Import"];
    private static readonly string[] Administration =
        ["Clients.Delete", "TeamConfiguration.ManageTeams", "TeamConfiguration.ManageStaffProfiles",
         "TeamConfiguration.ManageMemberships", "Partners.Delete", "PartnerDocuments.Delete"];

    private static FrozenSet<string> Permissions(params IEnumerable<string>[] groups) =>
        groups.SelectMany(group => group).Append("ReferenceData.View").ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, FrozenSet<string>> Grants =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["LimitedViewer"] = Permissions(["Clients.ViewAssigned"]),
            ["LimitedEditor"] = Permissions(["Clients.ViewAssigned", "Clients.Create", "Clients.EditAssigned"]),
            ["Viewer"] = Permissions(View, AdministrationView),
            ["Contributor"] = Permissions(View, AdministrationView, Contribution,
                ["Clients.Create", "Clients.EditAssigned", "Clients.ImportExport"]),
            ["Editor"] = Permissions(View, AdministrationView, Contribution,
                ["Clients.Create", "Clients.EditAssigned", "Clients.EditAll", "Clients.ImportExport"]),
            ["Admin"] = Permissions(View, AdministrationView, Contribution, Administration,
                ["Clients.Create", "Clients.EditAssigned", "Clients.EditAll", "Clients.ImportExport"]),
            ["Owner"] = Permissions(View, AdministrationView, Contribution, Administration,
                ["Clients.Create", "Clients.EditAssigned", "Clients.EditAll", "Clients.ImportExport"])
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static IReadOnlySet<string>? Resolve(string roleCode) =>
        Grants.GetValueOrDefault(roleCode);

    public static bool IsKnown(string permission) =>
        Grants.Values.Any(permissions => permissions.Contains(permission));
}
