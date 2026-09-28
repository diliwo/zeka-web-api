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
    public string? ExportInventoryJson { get; private set; }
    public string? ExportInventoryHash { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? SnapshotAt { get; private set; }
    public string? FenceEvidenceHash { get; private set; }
    public string? PackageSha256 { get; private set; }
    public string? PackageReference { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool IsActive { get; private set; } = true;
    private readonly List<LifecycleParticipant> participants = [];
    public IReadOnlyCollection<LifecycleParticipant> Participants => participants.AsReadOnly();

    public static LifecycleOperation Admit(Guid id, Guid organisationId, Guid subjectId,
        LifecycleOperationFamily family, Guid idempotencyId, LifecycleRegistry registry, DateTimeOffset now,
        LifecycleExportInventory? exportInventory = null)
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
        if (family == LifecycleOperationFamily.Export)
        {
            if (exportInventory is null) throw new ArgumentNullException(nameof(exportInventory));
            exportInventory.DemandRegistry(registry);
            operation.ExportInventoryJson = exportInventory.InventoryJson;
            operation.ExportInventoryHash = exportInventory.InventoryHash;
        }
        // Freeze the complete reviewed inventory, including capabilities for other families.
        operation.participants.AddRange(registry.Inventory.Select(b =>
            new LifecycleParticipant(id, organisationId, b)));
        return operation;
    }

    public bool IsReplay(Guid subject, LifecycleOperationFamily family, Guid idempotencyId) =>
        RequestingSubjectId == subject && Family == family && IdempotencyId == idempotencyId;

    public void BeginExport()
    {
        DemandExportState(LifecycleOperationState.Requested);
        State = LifecycleOperationState.EnteringFence;
        foreach (var participant in participants.Where(x =>
                     x.Family == LifecycleOperationFamily.Export
                     && x.CapabilityKey == "organisation.export-fence"))
            participant.Request();
        Revision++;
    }

    public void RecordFenceAccepted(string participantId)
    {
        DemandExportState(LifecycleOperationState.EnteringFence);
        FindExportParticipant(participantId, "organisation.export-fence").Accept();
    }

    public void EstablishSnapshot(DateTimeOffset snapshotAt, string fenceEvidenceHash)
    {
        DemandExportState(LifecycleOperationState.EnteringFence);
        if (participants.Where(IsFenceParticipant).Any(x => x.Mandatory && x.State != LifecycleParticipantState.Accepted))
            throw new InvalidOperationException("Every mandatory export fence must be accepted before SnapshotAt is established.");
        if (snapshotAt == default || snapshotAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("SnapshotAt must be a non-default UTC timestamp.", nameof(snapshotAt));
        SnapshotAt = snapshotAt;
        FenceEvidenceHash = RequireSha256(fenceEvidenceHash, nameof(fenceEvidenceHash));
        State = LifecycleOperationState.StagingFragments;
        foreach (var participant in participants.Where(x =>
                     x.Family == LifecycleOperationFamily.Export
                     && IsFragmentCapability(x.CapabilityKey)))
            participant.Request();
        Revision++;
    }

    public void RecordFragmentAccepted(string participantId)
    {
        DemandExportState(LifecycleOperationState.StagingFragments);
        FindExportParticipant(participantId, IsFragmentCapability).Accept();
        if (participants.Where(IsFragmentParticipant).All(x => !x.Mandatory || x.State == LifecycleParticipantState.Accepted))
        {
            State = LifecycleOperationState.AssemblingPackage;
            Revision++;
        }
    }

    public void PreparePackage(string packageSha256, DateTimeOffset completedAt)
    {
        DemandExportState(LifecycleOperationState.AssemblingPackage);
        if (completedAt == default || completedAt.Offset != TimeSpan.Zero || completedAt < SnapshotAt)
            throw new ArgumentException("Completion must be UTC and not precede SnapshotAt.", nameof(completedAt));
        var hash = RequireSha256(packageSha256, nameof(packageSha256));
        if (PackageSha256 is not null || CompletedAt is not null)
        {
            if (PackageSha256 != hash || CompletedAt != completedAt)
                throw new InvalidOperationException("Prepared export package identity is immutable.");
            return;
        }
        PackageSha256 = hash;
        CompletedAt = completedAt;
    }

    public void RecordPackage(string packageSha256, string packageReference)
    {
        DemandExportState(LifecycleOperationState.AssemblingPackage);
        var hash = RequireSha256(packageSha256, nameof(packageSha256));
        if (PackageSha256 != hash || CompletedAt is null)
            throw new InvalidOperationException("Export package must match its durable prepared identity.");
        PackageReference = RequireStable(packageReference, nameof(packageReference), 500);
        State = LifecycleOperationState.ReleasingFence;
        foreach (var participant in participants.Where(IsFenceParticipant)) participant.RequestRelease();
        Revision++;
    }

    public void RecordFenceReleased(string participantId)
    {
        DemandExportState(LifecycleOperationState.ReleasingFence);
        FindExportParticipant(participantId, "organisation.export-fence").Release();
        if (participants.Where(IsFenceParticipant).All(x => !x.Mandatory || x.State == LifecycleParticipantState.Released))
        {
            State = LifecycleOperationState.Completed;
            IsActive = false;
            Revision++;
        }
    }

    public void Fail(string failureCode)
    {
        if (!IsActive || State is LifecycleOperationState.Completed or LifecycleOperationState.Failed)
            throw new InvalidOperationException("Only an active lifecycle operation can fail.");
        FailureCode = RequireStable(failureCode, nameof(failureCode), 200);
        State = LifecycleOperationState.Failed;
        IsActive = false;
        Revision++;
    }

    private LifecycleParticipant FindExportParticipant(string participantId, string capability) =>
        participants.SingleOrDefault(x => x.Family == LifecycleOperationFamily.Export
            && x.ParticipantId == participantId && x.CapabilityKey == capability)
        ?? throw new InvalidOperationException("Participant is not part of the frozen admitted export inventory.");

    private LifecycleParticipant FindExportParticipant(string participantId, Func<string, bool> capability) =>
        participants.SingleOrDefault(x => x.Family == LifecycleOperationFamily.Export
            && x.ParticipantId == participantId && capability(x.CapabilityKey))
        ?? throw new InvalidOperationException("Participant is not part of the frozen admitted export inventory.");

    private static bool IsFenceParticipant(LifecycleParticipant participant) =>
        participant.Family == LifecycleOperationFamily.Export
        && participant.CapabilityKey == "organisation.export-fence";

    private static bool IsFragmentParticipant(LifecycleParticipant participant) =>
        participant.Family == LifecycleOperationFamily.Export
        && IsFragmentCapability(participant.CapabilityKey);

    private static bool IsFragmentCapability(string capability) =>
        capability.EndsWith(".export-fragment", StringComparison.Ordinal);

    private void DemandExportState(LifecycleOperationState expected)
    {
        if (Family != LifecycleOperationFamily.Export || State != expected || !IsActive)
            throw new InvalidOperationException($"Export operation must be active in {expected} state.");
    }

    private static string RequireSha256(string value, string name)
    {
        if (value?.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Value must be a lowercase SHA-256 hash.", name);
        return value;
    }

    private static string RequireStable(string value, string name, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim())
            throw new ArgumentException("Value must be a stable non-empty identifier.", name);
        return value;
    }
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

    internal void Request()
    {
        if (State != LifecycleParticipantState.Pending)
            throw new InvalidOperationException("Only a pending participant can be requested.");
        State = LifecycleParticipantState.Requested;
    }

    internal void Accept()
    {
        if (State == LifecycleParticipantState.Accepted) return;
        if (State != LifecycleParticipantState.Requested)
            throw new InvalidOperationException("Only a requested participant can be accepted.");
        State = LifecycleParticipantState.Accepted;
    }

    internal void RequestRelease()
    {
        if (State != LifecycleParticipantState.Accepted)
            throw new InvalidOperationException("Only an accepted fence can be released.");
        State = LifecycleParticipantState.ReleaseRequested;
    }

    internal void Release()
    {
        if (State == LifecycleParticipantState.Released) return;
        if (State != LifecycleParticipantState.ReleaseRequested)
            throw new InvalidOperationException("Only a requested release can be accepted.");
        State = LifecycleParticipantState.Released;
    }
}
