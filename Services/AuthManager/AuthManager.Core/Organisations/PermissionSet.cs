namespace AuthManager.Core.Organisations;

public sealed class PermissionSet
{
    public static readonly Guid OrganisationOwnerId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a01");
    public static readonly Guid OrganisationAdministratorId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a02");
    public static readonly Guid MemberId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a03");
    public static readonly Guid LimitedViewerId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a04");
    public static readonly Guid LimitedEditorId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a05");
    public static readonly Guid ViewerId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a06");
    public static readonly Guid ContributorId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a07");
    public static readonly Guid EditorId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a08");

    private PermissionSet()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public static IReadOnlyList<PermissionSet> SystemPermissionSets { get; } =
    [
        CreateSystem(OrganisationOwnerId, "Owner", "Owner"),
        CreateSystem(OrganisationAdministratorId, "Admin", "Admin"),
        CreateSystem(LimitedViewerId, "LimitedViewer", "Limited Viewer"),
        CreateSystem(LimitedEditorId, "LimitedEditor", "Limited Editor"),
        CreateSystem(ViewerId, "Viewer", "Viewer"),
        CreateSystem(ContributorId, "Contributor", "Contributor"),
        CreateSystem(EditorId, "Editor", "Editor")
    ];

    private static PermissionSet CreateSystem(Guid id, string code, string displayName) => new()
    {
        Id = id,
        Code = code,
        DisplayName = displayName,
        IsSystem = true
    };
}
