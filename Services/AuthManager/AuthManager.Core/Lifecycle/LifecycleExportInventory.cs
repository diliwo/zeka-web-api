using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthManager.Core.Lifecycle;

public sealed record LifecycleExportCategoryRequirement(
    string ParticipantId,
    string Category,
    IReadOnlyList<string> AllowedDispositions);

/// <summary>Canonical category/disposition inventory frozen into an admitted export operation.</summary>
public sealed class LifecycleExportInventory
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private LifecycleExportInventory(Guid registryRevision, string registryInventoryHash,
        LifecycleExportCategoryRequirement[] requirements)
    {
        RegistryRevision = registryRevision;
        RegistryInventoryHash = RequireHash(registryInventoryHash);
        Requirements = Array.AsReadOnly(requirements);
        InventoryJson = JsonSerializer.Serialize(requirements, Json);
        InventoryHash = Hash($"zeka-lifecycle-export-inventory-v1\n{RegistryRevision:D}\n{RegistryInventoryHash}\n{InventoryJson}");
    }

    public Guid RegistryRevision { get; }
    public string RegistryInventoryHash { get; }
    public IReadOnlyList<LifecycleExportCategoryRequirement> Requirements { get; }
    public string InventoryJson { get; }
    public string InventoryHash { get; }

    public static LifecycleExportInventory Create(LifecycleRegistry registry,
        IEnumerable<LifecycleExportCategoryRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(requirements);
        var fragmentParticipants = registry.Inventory
            .Where(x => x.Capability.Family == LifecycleOperationFamily.Export
                && x.Capability.Key.EndsWith(".export-fragment", StringComparison.Ordinal))
            .Select(x => x.ParticipantId).Order(StringComparer.Ordinal).ToArray();
        var ordered = requirements.Select(x => new LifecycleExportCategoryRequirement(
                Stable(x.ParticipantId, nameof(x.ParticipantId)),
                Stable(x.Category, nameof(x.Category)),
                Array.AsReadOnly(x.AllowedDispositions.Select(d => Stable(d, nameof(x.AllowedDispositions)))
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray())))
            .OrderBy(x => x.ParticipantId, StringComparer.Ordinal)
            .ThenBy(x => x.Category, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0 || ordered.Any(x => x.AllowedDispositions.Count == 0)
            || ordered.DistinctBy(x => (x.ParticipantId, x.Category)).Count() != ordered.Length
            || !fragmentParticipants.SequenceEqual(ordered.Select(x => x.ParticipantId)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new ArgumentException("Every frozen fragment participant and category must be represented exactly once.", nameof(requirements));
        return new LifecycleExportInventory(registry.Revision, registry.InventoryHash, ordered);
    }

    public static LifecycleExportInventory Rehydrate(Guid registryRevision, string registryInventoryHash,
        string inventoryJson, string inventoryHash)
    {
        var requirements = JsonSerializer.Deserialize<LifecycleExportCategoryRequirement[]>(inventoryJson, Json)
            ?? throw new InvalidOperationException("Frozen export inventory is unavailable.");
        var value = new LifecycleExportInventory(registryRevision, registryInventoryHash, requirements);
        if (!string.Equals(value.InventoryJson, inventoryJson, StringComparison.Ordinal)
            || !string.Equals(value.InventoryHash, inventoryHash, StringComparison.Ordinal))
            throw new InvalidOperationException("Frozen export inventory does not match its canonical hash.");
        return value;
    }

    public IReadOnlyList<LifecycleExportCategoryRequirement> For(string participantId) =>
        Requirements.Where(x => x.ParticipantId == participantId).ToArray();

    public void DemandRegistry(LifecycleRegistry registry)
    {
        if (RegistryRevision != registry.Revision || RegistryInventoryHash != registry.InventoryHash)
            throw new InvalidOperationException("Export inventory must bind to the admitted registry identity.");
    }

    private static string Stable(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 200
            ? value : throw new ArgumentException("A stable inventory key is required.", name);
    private static string RequireHash(string value) => value?.Length == 64
        && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value : throw new ArgumentException("A lowercase SHA-256 hash is required.");
    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
