using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Lifecycle;

public enum LifecycleClosureBoundaryDisposition
{
    Unknown = 0,
    NotEstablished = 1
}

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
    public DateTimeOffset? ClosingAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public string? ClosureFenceEvidenceHash { get; private set; }
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

    public void BeginTermination(DateTimeOffset closingAt)
    {
        DemandTerminationState(LifecycleOperationState.Pending);
        if (closingAt == default || closingAt.Offset != TimeSpan.Zero || closingAt < RequestedAt)
            throw new ArgumentException("ClosingAt must be UTC and not precede admission.", nameof(closingAt));
        ClosingAt = closingAt;
        State = LifecycleOperationState.EnteringFence;
        foreach (var participant in ClosureParticipants()) participant.Request();
        Revision++;
    }

    public void RecordClosureAccepted(string participantId)
    {
        DemandTerminationState(LifecycleOperationState.EnteringFence);
        FindClosureParticipant(participantId).Accept();
    }

    public void RecordClosureFailure(string participantId, string failureCode, bool retryable,
        DateTimeOffset failedAt, LifecycleClosureBoundaryDisposition boundaryDisposition)
    {
        DemandTerminationState(LifecycleOperationState.EnteringFence);
        FindClosureParticipant(participantId).Fail(failureCode, retryable, failedAt, boundaryDisposition);
    }

    public void CompleteTermination(DateTimeOffset archivedAt, string evidenceHash)
    {
        DemandTerminationState(LifecycleOperationState.EnteringFence);
        if (ClosureParticipants().Any(x => x.Mandatory && x.State != LifecycleParticipantState.Accepted))
            throw new InvalidOperationException("Every mandatory closure fence must be accepted before archive.");
        if (archivedAt == default || archivedAt.Offset != TimeSpan.Zero || archivedAt < ClosingAt)
            throw new ArgumentException("ArchivedAt must be UTC and not precede ClosingAt.", nameof(archivedAt));
        ArchivedAt = archivedAt;
        ClosureFenceEvidenceHash = RequireSha256(evidenceHash, nameof(evidenceHash));
        CompletedAt = archivedAt;
        State = LifecycleOperationState.Completed;
        IsActive = false;
        Revision++;
    }

    public void BeginClosureRecovery()
    {
        DemandTerminationState(LifecycleOperationState.EnteringFence);
        var closure = ClosureParticipants().ToArray();
        if (closure.Any(x => x.State is LifecycleParticipantState.Pending
                or LifecycleParticipantState.Requested))
            throw new InvalidOperationException(
                "Closure recovery cannot begin while a frozen participant remains pending or requested.");
        if (closure.Any(x => x.State == LifecycleParticipantState.Failed
                && (x.FailedAt is null || x.FailureRetryable is null
                    || string.IsNullOrWhiteSpace(x.FailureCode)
                    || x.FailureBoundaryDisposition != LifecycleClosureBoundaryDisposition.NotEstablished)))
            throw new InvalidOperationException(
                "Closure recovery requires an explicit durable participant failure.");
        State = LifecycleOperationState.ReleasingFence;
        foreach (var participant in closure.Where(x => x.State == LifecycleParticipantState.Accepted))
            participant.RequestRelease();
        Revision++;
    }

    public void RecordClosureReleased(string participantId)
    {
        DemandTerminationState(LifecycleOperationState.ReleasingFence);
        FindClosureParticipant(participantId).Release();
    }

    public bool CanCompleteClosureRecovery() =>
        Family == LifecycleOperationFamily.Termination
        && State == LifecycleOperationState.ReleasingFence
        && IsActive
        && ClosureParticipants().All(x => x.State is LifecycleParticipantState.Released
            or LifecycleParticipantState.Failed);

    public void CompleteClosureRecovery(DateTimeOffset recoveredAt)
    {
        DemandTerminationState(LifecycleOperationState.ReleasingFence);
        if (!CanCompleteClosureRecovery())
            throw new InvalidOperationException("Every established closure fence must be released before recovery.");
        CompletedAt = recoveredAt == default || recoveredAt.Offset != TimeSpan.Zero
            ? throw new ArgumentException("Recovery timestamp must be non-default UTC.", nameof(recoveredAt))
            : recoveredAt;
        FailureCode = "ClosureRecoveredBeforeArchive";
        State = LifecycleOperationState.Failed;
        IsActive = false;
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

    private IEnumerable<LifecycleParticipant> ClosureParticipants() =>
        participants.Where(x => x.Family == LifecycleOperationFamily.Termination
            && x.CapabilityKey == "organisation.closure-fence");

    private LifecycleParticipant FindClosureParticipant(string participantId) =>
        ClosureParticipants().SingleOrDefault(x => x.ParticipantId == participantId)
        ?? throw new InvalidOperationException("Participant is not part of the frozen admitted closure inventory.");

    private void DemandExportState(LifecycleOperationState expected)
    {
        if (Family != LifecycleOperationFamily.Export || State != expected || !IsActive)
            throw new InvalidOperationException($"Export operation must be active in {expected} state.");
    }

    private void DemandTerminationState(LifecycleOperationState expected)
    {
        if (Family != LifecycleOperationFamily.Termination || State != expected || !IsActive)
            throw new InvalidOperationException($"Termination operation must be active in {expected} state.");
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
    public string? FailureCode { get; private set; }
    public bool? FailureRetryable { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public LifecycleClosureBoundaryDisposition? FailureBoundaryDisposition { get; private set; }

    internal void Request()
    {
        if (State != LifecycleParticipantState.Pending)
            throw new InvalidOperationException("Only a pending participant can be requested.");
        State = LifecycleParticipantState.Requested;
    }

    internal void Accept()
    {
        if (State == LifecycleParticipantState.Accepted) return;
        if (State is not (LifecycleParticipantState.Requested or LifecycleParticipantState.Failed))
            throw new InvalidOperationException("Only a requested participant can be accepted.");
        State = LifecycleParticipantState.Accepted;
        FailureCode = null;
        FailureRetryable = null;
        FailedAt = null;
        FailureBoundaryDisposition = null;
    }

    internal void Fail(string failureCode, bool retryable, DateTimeOffset failedAt,
        LifecycleClosureBoundaryDisposition boundaryDisposition)
    {
        if (State is not (LifecycleParticipantState.Requested or LifecycleParticipantState.Failed))
            throw new InvalidOperationException("Only a requested participant can report a failure.");
        FailureCode = RequireParticipantValue(failureCode, nameof(failureCode));
        FailureRetryable = retryable;
        FailedAt = failedAt != default && failedAt.Offset == TimeSpan.Zero
            ? failedAt : throw new ArgumentException("Failure timestamp must be non-default UTC.", nameof(failedAt));
        FailureBoundaryDisposition = boundaryDisposition;
        State = LifecycleParticipantState.Failed;
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

    private static string RequireParticipantValue(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 200
            ? value : throw new ArgumentException("Value must be a stable non-empty identifier.", name);
}
