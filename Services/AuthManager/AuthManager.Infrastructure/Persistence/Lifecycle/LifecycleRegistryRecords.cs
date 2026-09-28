using AuthManager.Core.Lifecycle;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

public sealed class LifecycleRegistryRevisionRecord
{
    public Guid Id { get; set; }
    public string ReviewReference { get; set; } = "";
    public string InventoryHash { get; set; } = "";
    public List<LifecycleRegistryBindingRecord> Bindings { get; set; } = [];
    public LifecycleRegistry ToRegistry()
    {
        var inventory = Bindings.Select(b => new LifecycleBinding(
            new(b.Family, b.CapabilityKey, b.OwnershipScope), b.ParticipantId, b.ContractVersion, b.Mandatory)).ToArray();
        var result = LifecycleRegistry.Validate(Id, ReviewReference, inventory.Select(b => b.Capability), inventory);
        if (result.Registry is not { } registry || registry.InventoryHash != InventoryHash)
            throw new InvalidOperationException("Persisted lifecycle registry is invalid.");
        return registry;
    }
    public static LifecycleRegistryRevisionRecord From(LifecycleRegistry registry) => new()
    {
        Id = registry.Revision, ReviewReference = registry.ReviewReference, InventoryHash = registry.InventoryHash,
        Bindings = registry.Inventory.Select(b => new LifecycleRegistryBindingRecord
        {
            RevisionId = registry.Revision, Family = b.Capability.Family, CapabilityKey = b.Capability.Key,
            OwnershipScope = b.Capability.Scope, ParticipantId = b.ParticipantId,
            ContractVersion = b.ContractVersion, Mandatory = b.Mandatory
        }).ToList()
    };
}
public sealed class LifecycleRegistryBindingRecord
{
    public Guid RevisionId { get; set; }
    public LifecycleOperationFamily Family { get; set; }
    public string CapabilityKey { get; set; } = "";
    public string OwnershipScope { get; set; } = "";
    public string ParticipantId { get; set; } = "";
    public int ContractVersion { get; set; }
    public bool Mandatory { get; set; }
}
public sealed class LifecycleRegistryActivationRecord
{
    public int Id { get; set; } = 1;
    public Guid RevisionId { get; set; }
    public long Version { get; set; }
}
