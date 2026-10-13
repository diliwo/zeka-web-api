using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public sealed class IdempotencyReceipt : TenantOwnedEntity
{
    public Guid ActorMembershipId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string ClientKey { get; set; } = string.Empty;
    public int ClientId { get; set; }
    // Canonical metadata excludes note text. On duplicate, compare the stored Interaction.Note directly.
    public string CanonicalMetadata { get; set; } = string.Empty;
    public int? InteractionId { get; set; }
    public int? FollowUpActionId { get; set; }
    public int? ActionChangeId { get; set; }
    public int ResultVersion { get; set; }
    public DateTime RetainUntilUtc { get; set; }
}
