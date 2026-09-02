namespace AuthManager.Core.Organisations;

public sealed class PermissionSet
{
    public static readonly Guid OrganisationOwnerId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a01");
    public static readonly Guid OrganisationAdministratorId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a02");
    public static readonly Guid MemberId = Guid.Parse("d8eceeeb-b796-4d77-92a5-6a11fab10a03");

    private PermissionSet()
    {
    }

    public Guid Id { get; private set; }

    public string Code { get; private set; } = string.Empty;

    public string DisplayName { get; private set; } = string.Empty;

    public bool IsSystem { get; private set; }

    public static IReadOnlyList<PermissionSet> SystemPermissionSets { get; } =
    [
        CreateSystem(OrganisationOwnerId, "OrganisationOwner", "Organisation owner"),
        CreateSystem(OrganisationAdministratorId, "OrganisationAdministrator", "Organisation administrator"),
        CreateSystem(MemberId, "Member", "Member")
    ];

    private static PermissionSet CreateSystem(Guid id, string code, string displayName) => new()
    {
        Id = id,
        Code = code,
        DisplayName = displayName,
        IsSystem = true
    };
}
