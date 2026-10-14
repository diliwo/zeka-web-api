using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public sealed class FollowUpAction : TenantOwnedEntity
{
    public int ClientId { get; set; }
    public int SocialCaseId { get; set; }
    public int InteractionId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public DateOnly DueLocalDate { get; set; }
    public Guid AssigneeMembershipId { get; set; }
    public Guid CreatorMembershipId { get; set; }
    public string State { get; set; } = "Open";
    public int Version { get; set; } = 1;
}
