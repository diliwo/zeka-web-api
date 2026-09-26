using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

public sealed class LifecycleOperation : ITenantOwnedEntity
{
    private LifecycleOperation() { }
    public Guid Id { get; private set; }
    public Guid OrganisationId { get; private set; }
    public LifecycleOperationFamily Family { get; private set; }
    public LifecycleOperationState State { get; private set; }
    public Guid RequestingSubjectId { get; private set; }
    public Guid IdempotencyId { get; private set; }
    public Guid RegistryRevision { get; private set; }
    public string InventoryHash { get; private set; } = "";
    public DateTimeOffset RequestedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool IsActive { get; private set; } = true;
    private readonly List<LifecycleParticipant> participants = [];
    public IReadOnlyCollection<LifecycleParticipant> Participants => participants.AsReadOnly();

    public static LifecycleOperation Admit(Guid id, Guid organisationId, Guid subjectId,
        LifecycleOperationFamily family, Guid idempotencyId, LifecycleRegistry registry, DateTimeOffset now)
    {
        if (id == Guid.Empty || organisationId == Guid.Empty || subjectId == Guid.Empty
            || idempotencyId == Guid.Empty || !Enum.IsDefined(family))
            throw new ArgumentException("Lifecycle admission identity is invalid.");
        var operation = new LifecycleOperation
        {
            Id = id, OrganisationId = organisationId, RequestingSubjectId = subjectId,
            Family = family, IdempotencyId = idempotencyId, RegistryRevision = registry.Revision,
            InventoryHash = registry.InventoryHash, RequestedAt = now,
            State = family == LifecycleOperationFamily.Export
                ? LifecycleOperationState.Requested : LifecycleOperationState.Pending
        };
        // Freeze the complete reviewed inventory, including capabilities for other families.
        operation.participants.AddRange(registry.Inventory.Select(b =>
            new LifecycleParticipant(id, organisationId, b)));
        return operation;
    }

    public bool IsReplay(Guid subject, LifecycleOperationFamily family, Guid idempotencyId) =>
        RequestingSubjectId == subject && Family == family && IdempotencyId == idempotencyId;
}

public sealed class LifecycleParticipant : ITenantOwnedEntity
{
    private LifecycleParticipant() { }
    internal LifecycleParticipant(Guid operation, Guid organisation, LifecycleBinding binding)
    {
        OperationId = operation; OrganisationId = organisation; ParticipantId = binding.ParticipantId;
        Family = binding.Capability.Family; CapabilityKey = binding.Capability.Key;
        OwnershipScope = binding.Capability.Scope; ContractVersion = binding.ContractVersion;
        Mandatory = binding.Mandatory;
    }
    public Guid OperationId { get; private set; }
    public Guid OrganisationId { get; private set; }
    public string ParticipantId { get; private set; } = "";
    public LifecycleOperationFamily Family { get; private set; }
    public string CapabilityKey { get; private set; } = "";
    public string OwnershipScope { get; private set; } = "";
    public int ContractVersion { get; private set; }
    public bool Mandatory { get; private set; }
    public LifecycleParticipantState State { get; private set; } = LifecycleParticipantState.Pending;
}
