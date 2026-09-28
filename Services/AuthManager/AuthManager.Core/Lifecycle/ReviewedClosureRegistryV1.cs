namespace AuthManager.Core.Lifecycle;

public static class ReviewedClosureRegistryV1
{
    public static readonly Guid Revision = Guid.Parse("46020000-0000-0000-0000-000000000002");
    public const string ReviewReference = "Issue46-LIFE-02-v1";
    public const string CapabilityKey = "organisation.closure-fence";

    public static LifecycleRegistry Create()
    {
        var bindings = new[]
        {
            ExportBinding("auth-management", "organisation.export-fence",
                "auth-management"),
            ExportBinding("admin-area", "organisation.export-fence",
                "admin-area"),
            ExportBinding("client-management", "organisation.export-fence",
                "client-management"),
            ExportBinding("auth-management", "auth-management.export-fragment",
                "auth-management"),
            ExportBinding("admin-area", "admin-area.export-fragment", "admin-area"),
            ExportBinding("admin-area-documents", "admin-area-documents.export-fragment",
                "admin-area"),
            ExportBinding("client-management", "client-management.export-fragment",
                "client-management"),
            ClosureBinding("auth-management", "auth-management-owned-records"),
            ClosureBinding("admin-area", "admin-area-owned-records"),
            ClosureBinding("admin-area-documents", "admin-area-document-storage"),
            ClosureBinding("client-management", "client-management-owned-records")
        };
        var validation = LifecycleRegistry.Validate(Revision, ReviewReference,
            bindings.Select(x => x.Capability), bindings);
        return validation.Registry
            ?? throw new InvalidOperationException($"Reviewed LIFE-02 registry is invalid: {validation.Error}.");
    }

    private static LifecycleBinding ClosureBinding(string participant, string scope) => new(
        new LifecycleCapability(LifecycleOperationFamily.Termination, CapabilityKey, scope),
        participant, 1, true);

    private static LifecycleBinding ExportBinding(string participant, string capability, string scope) => new(
        new LifecycleCapability(LifecycleOperationFamily.Export, capability, scope),
        participant, 1, true);
}
