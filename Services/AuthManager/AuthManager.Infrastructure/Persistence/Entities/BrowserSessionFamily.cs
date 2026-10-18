namespace AuthManager.Infrastructure.Persistence.Entities;

// Tenant-neutral Identity persistence. A family never carries organisation or permission authority.
public sealed class BrowserSessionFamily
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset IdleExpiresAtUtc { get; set; }
    public DateTimeOffset AbsoluteExpiresAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class BrowserRefreshCredential
{
    public string Hash { get; set; } = "";
    public Guid FamilyId { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? ConsumedAtUtc { get; set; }
}
