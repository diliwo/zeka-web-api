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
    public static readonly Guid StaffProjectionWorkerId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a09");
    public const string StaffProjectionWorkerCode = "StaffProjectionWorker";

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

    // The approved worker profile is provisioned separately from the human-role
    // migration seeds. Declaring it never creates a database row or membership.
    public static PermissionSet CreateStaffProjectionWorker() =>
        CreateSystem(StaffProjectionWorkerId, StaffProjectionWorkerCode, "Staff Projection Worker");

    private static PermissionSet CreateSystem(Guid id, string code, string displayName) => new()
    {
        Id = id,
        Code = code,
        DisplayName = displayName,
        IsSystem = true
    };
}
