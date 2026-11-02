namespace AuthManager.Application.Workers;

public static class WorkerProfiles
{
    public const string AdminAreaRetry = "adminarea-retry";
    public const string ClientProjection = "client-projection";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { AdminAreaRetry, ClientProjection });
}

public sealed class WorkerRotationOptions
{
    public bool Enabled { get; init; }
    public bool LocalStack { get; init; }
    public Guid OwnerSubjectId { get; init; }
    public Guid OrganisationId { get; init; }
    public TimeSpan ProcessingTimeout { get; init; }
    public TimeSpan Retention { get; init; }
}

public sealed record WorkerRotationCaller(Guid SubjectId, Guid SessionId);
public sealed record WorkerRotationRequest(Guid OperationId, Guid OrganisationId);
public sealed record WorkerUseWitness(string Profile, long Generation, Guid SessionId,
    string PublicationSha256, string SuccessfulUseEvidenceRef, int ProcessId, string ContainerId, Guid ProcessInstanceId);
public sealed record WorkerFinalizeRequest(Guid OperationId, Guid OrganisationId, IReadOnlyList<WorkerUseWitness> Witnesses);

// This private TLS response is never persisted, logged, or included in an operational receipt.
public sealed class WorkerPreparedIdentity
{
    public required string Profile { get; init; }
    public Guid SubjectId { get; init; }
    public Guid MembershipId { get; init; }
    public Guid SessionId { get; init; }
    public long Generation { get; init; }
    public DateTimeOffset SessionCreatedAtUtc { get; init; }
    public DateTimeOffset TokenIssuedAtUtc { get; init; }
    public DateTimeOffset AccessExpiresAtUtc { get; init; }
    public DateTimeOffset SessionExpiresAtUtc { get; init; }
    public string? Token { get; init; }
    public override string ToString() => $"Worker identity: {Profile}, generation {Generation}";
}
public sealed class WorkerRotationResult
{
    public Guid OperationId { get; init; }
    public Guid OrganisationId { get; init; }
    public required string State { get; init; }
    public IReadOnlyList<WorkerPreparedIdentity> Workers { get; init; } = [];
    public override string ToString() => $"Worker rotation: {OperationId:D}, {State}";
}

public interface IWorkerRotationStore
{
    Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller, WorkerRotationRequest request,
        IReadOnlyList<WorkerUseWitness>? witnesses, CancellationToken cancellationToken);
}
public sealed class WorkerRotationException(string code, bool requiresInspection = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool RequiresInspection { get; } = requiresInspection;
}

public sealed class WorkerRotationService(IWorkerRotationStore store, WorkerRotationOptions options)
{
    public Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller,
        WorkerRotationRequest request, IReadOnlyList<WorkerUseWitness>? witnesses = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || !options.LocalStack || options.OwnerSubjectId == Guid.Empty
            || options.OrganisationId == Guid.Empty || options.ProcessingTimeout <= TimeSpan.Zero
            || options.Retention <= options.ProcessingTimeout)
            throw new WorkerRotationException("WORKER_ROTATION_DISABLED_OR_UNCONFIGURED");
        if (caller.SubjectId != options.OwnerSubjectId || caller.SessionId == Guid.Empty
            || request.OrganisationId != options.OrganisationId || request.OperationId == Guid.Empty)
            throw new WorkerRotationException("WORKER_ROTATION_OWNER_SCOPE_REJECTED");
        if (phase is not ("prepare" or "begin-cutover" or "finalize" or "abort" or "inspect"))
            throw new WorkerRotationException("WORKER_ROTATION_PHASE_REJECTED");
        if (phase == "finalize" && !ValidWitnesses(witnesses))
            throw new WorkerRotationException("WORKER_ROTATION_BOTH_SUCCESSFUL_USES_REQUIRED");
        return store.ExecuteAsync(phase, caller, request, witnesses, cancellationToken);
    }

    public static bool ValidWitnesses(IReadOnlyList<WorkerUseWitness>? witnesses) => witnesses is { Count: 2 }
        && witnesses.All(value => value is not null)
        && WorkerProfiles.All.All(profile => witnesses.Count(value => value.Profile == profile) == 1)
        && witnesses.All(value => value.Generation > 0 && value.SessionId != Guid.Empty
            && value.PublicationSha256 is { Length: 64 } && value.PublicationSha256.All(Uri.IsHexDigit)
            && !string.IsNullOrWhiteSpace(value.SuccessfulUseEvidenceRef) && value.SuccessfulUseEvidenceRef.Length <= 200
            && value.SuccessfulUseEvidenceRef.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or '/')
            && value.ProcessId > 0 && value.ProcessInstanceId != Guid.Empty
            && value.ContainerId is { Length: 64 } && value.ContainerId.All(Uri.IsHexDigit));
}
