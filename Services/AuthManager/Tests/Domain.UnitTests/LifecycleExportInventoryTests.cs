using AuthManager.Core.Lifecycle;

namespace Domain.UnitTests;

public sealed class LifecycleExportInventoryTests
{
    [Fact]
    public void Canonical_inventory_is_registry_bound_and_tamper_evident()
    {
        var firstRegistry = Registry(Guid.Parse("46010000-0000-0000-0000-000000000101"));
        var requirements = new[]
        {
            new LifecycleExportCategoryRequirement("auth-management", "memberships", ["included", "empty"]),
            new LifecycleExportCategoryRequirement("auth-management", "audit-events", ["withheld"])
        };
        var first = LifecycleExportInventory.Create(firstRegistry, requirements);
        var reordered = LifecycleExportInventory.Create(firstRegistry, requirements.Reverse());
        Assert.Equal(first.InventoryJson, reordered.InventoryJson);
        Assert.Equal(first.InventoryHash, reordered.InventoryHash);
        Assert.Equal(first.InventoryHash, LifecycleExportInventory.Rehydrate(first.RegistryRevision,
            first.RegistryInventoryHash, first.InventoryJson, first.InventoryHash).InventoryHash);
        Assert.Throws<InvalidOperationException>(() => LifecycleExportInventory.Rehydrate(
            first.RegistryRevision, first.RegistryInventoryHash,
            first.InventoryJson.Replace("memberships", "membershipz", StringComparison.Ordinal),
            first.InventoryHash));

        var successor = LifecycleExportInventory.Create(
            Registry(Guid.Parse("46010000-0000-0000-0000-000000000102")), requirements);
        Assert.NotEqual(first.InventoryHash, successor.InventoryHash);
        Assert.Throws<ArgumentException>(() => LifecycleExportInventory.Create(firstRegistry,
            Array.Empty<LifecycleExportCategoryRequirement>()));
    }

    private static LifecycleRegistry Registry(Guid revision)
    {
        var capability = new LifecycleCapability(LifecycleOperationFamily.Export,
            "auth-management.export-fragment", "auth-management");
        return LifecycleRegistry.Validate(revision, "Issue46-LIFE-01-inventory-test", [capability],
            [new LifecycleBinding(capability, "auth-management", 1, true)]).Registry!;
    }
}
