using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthManager.Core.Lifecycle;

public enum LifecycleOperationFamily { Export, Termination }
public enum LifecycleOperationState
{
    Requested,
    Pending,
    EnteringFence,
    StagingFragments,
    AssemblingPackage,
    ReleasingFence,
    Completed,
    Failed,
    Archived,
    DispositionReady,
    PurgeInProgress,
    PurgeExecutionComplete,
    VerifiedPurged
}

public enum LifecycleParticipantState
{
    Pending,
    Requested,
    Accepted,
    ReleaseRequested,
    Released,
    Failed
}

public sealed record LifecycleCapability(LifecycleOperationFamily Family, string Key, string Scope);
public sealed record LifecycleBinding(LifecycleCapability Capability, string ParticipantId,
    int ContractVersion, bool Mandatory);

public enum RegistryValidationError
{
    InvalidIdentity, MissingOwner, OverlappingOwner, DuplicateBinding, IncompleteInventory,
    UnsupportedContract
}

/// <summary>Architecture-reviewed input. Never populated from service discovery or health.</summary>
public sealed class LifecycleRegistry
{
    private LifecycleRegistry(Guid revision, string reviewReference, LifecycleBinding[] inventory)
    {
        Revision = revision;
        ReviewReference = reviewReference;
        Inventory = Array.AsReadOnly(inventory);
        // An explicit format version and JSON arrays provide unambiguous UTF-8 framing.
        InventoryJson = JsonSerializer.Serialize(inventory.Select(b => new object[]
        { (int)b.Capability.Family, b.Capability.Key, b.Capability.Scope, b.ParticipantId,
            b.ContractVersion, b.Mandatory }));
        InventoryHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("zeka-lifecycle-inventory-v1\n" + InventoryJson))).ToLowerInvariant();
    }

    public Guid Revision { get; }
    public string ReviewReference { get; }
    public IReadOnlyList<LifecycleBinding> Inventory { get; }
    public string InventoryJson { get; }
    public string InventoryHash { get; }

    public static (LifecycleRegistry? Registry, RegistryValidationError? Error) Validate(
        Guid revision, string reviewReference, IEnumerable<LifecycleCapability> required,
        IEnumerable<LifecycleBinding> bindings)
    {
        var requirements = required.ToArray();
        var inventory = bindings.ToArray();
        if (revision == Guid.Empty || !Valid(reviewReference) || requirements.Length == 0
            || requirements.Any(c => !Valid(c.Key) || !Valid(c.Scope) || !Enum.IsDefined(c.Family))
            || requirements.Distinct().Count() != requirements.Length
            || inventory.Any(b => !Valid(b.ParticipantId) || !Valid(b.Capability.Key)
                || !Valid(b.Capability.Scope) || !Enum.IsDefined(b.Capability.Family)))
            return (null, RegistryValidationError.InvalidIdentity);
        if (inventory.Any(b => b.ContractVersion != 1))
            return (null, RegistryValidationError.UnsupportedContract);
        if (inventory.DistinctBy(b => (b.Capability, b.ParticipantId)).Count() != inventory.Length)
            return (null, RegistryValidationError.DuplicateBinding);
        if (inventory.GroupBy(b => b.Capability).Any(g => g.Count() != 1))
            return (null, RegistryValidationError.OverlappingOwner);
        if (requirements.Any(c => !inventory.Any(b => b.Capability == c)))
            return (null, RegistryValidationError.MissingOwner);
        if (inventory.Any(b => !requirements.Contains(b.Capability) || !b.Mandatory))
            return (null, RegistryValidationError.IncompleteInventory);
        return (new LifecycleRegistry(revision, reviewReference, inventory
            .OrderBy(b => b.Capability.Family).ThenBy(b => b.Capability.Key, StringComparer.Ordinal)
            .ThenBy(b => b.Capability.Scope, StringComparer.Ordinal)
            .ThenBy(b => b.ParticipantId, StringComparer.Ordinal).ToArray()), null);
    }

    private static bool Valid(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 200 && value == value.Trim();
}
