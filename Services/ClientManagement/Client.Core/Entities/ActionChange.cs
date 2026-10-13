using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

// Insert-only attributed history. Runtime role has no UPDATE or DELETE grant.
public sealed class ActionChange : TenantOwnedEntity
{
    public int FollowUpActionId { get; set; }
    public Guid ActorMembershipId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public int PriorVersion { get; set; }
    public int NewVersion { get; set; }
    public string PriorState { get; set; } = string.Empty;
    public string NewState { get; set; } = string.Empty;
    public Guid PriorAssigneeMembershipId { get; set; }
    public Guid NewAssigneeMembershipId { get; set; }
    public DateOnly PriorDueLocalDate { get; set; }
    public DateOnly NewDueLocalDate { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}
