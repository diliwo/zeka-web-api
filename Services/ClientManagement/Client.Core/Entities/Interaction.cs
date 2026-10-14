using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

// Separate from the mutable legacy SocialCase.Note. Corrections are a later contract.
public sealed class Interaction : TenantOwnedEntity
{
    public int ClientId { get; set; }
    public int SocialCaseId { get; set; }
    public Guid AuthorMembershipId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Note { get; set; } = string.Empty;
    public int Revision { get; set; } = 1;
}
