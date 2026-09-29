using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthManager.Core.Lifecycle;

/// <summary>
/// Reviewed non-production LIFE-03 ownership inventory. The four categories are conservative
/// owner-scoped aggregates: a PURGE decision must cover the whole category, never selected rows.
/// Registry creation does not activate the revision in a production environment.
/// </summary>
public static class ReviewedDispositionRegistryV1
{
    public static readonly Guid Revision = Guid.Parse("46030000-0000-0000-0000-000000000003");
    public const string ReviewReference = "Issue46-LIFE-03-v1";
    public const string CapabilityKey = "organisation.disposition-category";

    private static readonly (string Participant, string Category)[] Categories =
    [
        ("auth-management", "auth-management-owned-records"),
        ("admin-area", "admin-area-owned-records"),
        ("admin-area-documents", "admin-area-document-storage"),
        ("client-management", "client-management-owned-records")
    ];

    public static LifecycleRegistry Create()
    {
        var bindings = ReviewedClosureRegistryV1.Create().Inventory.Concat(
            Categories.Select(x => new LifecycleBinding(
                new LifecycleCapability(LifecycleOperationFamily.Termination, CapabilityKey, x.Category),
                x.Participant, 1, true))).ToArray();
        var validation = LifecycleRegistry.Validate(Revision, ReviewReference,
            bindings.Select(x => x.Capability), bindings);
        return validation.Registry is { } registry && HasCompleteInventory(registry)
            ? registry : throw new InvalidOperationException("Reviewed LIFE-03 registry is incomplete.");
    }

    public static bool HasCompleteInventory(LifecycleRegistry registry)
    {
        var actual = registry.Inventory.Where(x => x.Capability.Family == LifecycleOperationFamily.Termination
            && x.Capability.Key == CapabilityKey).ToArray();
        return registry.Revision == Revision && actual.Length == Categories.Length
            && Categories.All(category => actual.Count(binding => binding.ParticipantId == category.Participant
                && binding.Capability.Scope == category.Category && binding.ContractVersion == 1
                && binding.Mandatory) == 1);
    }

    public static string? FrozenCategoryHash(IEnumerable<LifecycleParticipant> participants)
    {
        var categories = participants.Where(x => x.Family == LifecycleOperationFamily.Termination
            && x.CapabilityKey == CapabilityKey)
            .OrderBy(x => x.OwnershipScope, StringComparer.Ordinal).ToArray();
        if (categories.Length == 0) return null;
        var json = JsonSerializer.Serialize(categories.Select(x => new object[]
        { x.OwnershipScope, x.ParticipantId, x.ContractVersion, x.Mandatory }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "zeka-disposition-inventory-v1\n" + json))).ToLowerInvariant();
    }

    public static bool HasCompleteFrozenInventory(IEnumerable<LifecycleParticipant> participants)
    {
        var actual = participants.Where(x => x.Family == LifecycleOperationFamily.Termination
            && x.CapabilityKey == CapabilityKey).ToArray();
        return actual.Length == Categories.Length
            && Categories.All(category => actual.Count(x => x.ParticipantId == category.Participant
                && x.OwnershipScope == category.Category && x.ContractVersion == 1
                && x.Mandatory) == 1);
    }
}
