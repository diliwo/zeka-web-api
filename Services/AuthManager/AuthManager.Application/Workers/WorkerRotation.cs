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
    public Guid RegistryId { get; init; }
    public TimeSpan ProcessingTimeout { get; init; }
    public TimeSpan Retention { get; init; }
}

public sealed record WorkerRotationCaller(Guid SubjectId, Guid SessionId);
public sealed record WorkerRotationRequest(Guid OperationId, Guid OrganisationId);
public sealed record WorkerDeliveryReceipt(string Profile, Guid OperationId, long Generation, Guid SubjectId, Guid SessionId,
    string PayloadSha256, string PublicationSha256, string VolumeName, string DestinationPath,
    int OwnerUid, int OwnerGid, string DirectoryMode, string Mode, string ConsumerService, bool ReadOnlyMount);
public sealed record WorkerRevokeOthersRequest(Guid OperationId, Guid OrganisationId, IReadOnlyList<WorkerDeliveryReceipt> Deliveries);
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
    public string? PayloadSha256 { get; init; }
    public string? PublicationSha256 { get; init; }
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
        IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken cancellationToken);
}
public sealed class WorkerRotationException(string code, bool requiresInspection = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool RequiresInspection { get; } = requiresInspection;
}

public sealed class WorkerRotationService(IWorkerRotationStore store, WorkerRotationOptions options)
{
    public Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller,
        WorkerRotationRequest request, IReadOnlyList<WorkerDeliveryReceipt>? deliveries = null,
        CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || !options.LocalStack || options.OwnerSubjectId == Guid.Empty
            || options.OrganisationId == Guid.Empty || options.RegistryId == Guid.Empty || options.ProcessingTimeout <= TimeSpan.Zero
            || options.Retention <= options.ProcessingTimeout)
            throw new WorkerRotationException("WORKER_ROTATION_DISABLED_OR_UNCONFIGURED");
        if (caller.SubjectId != options.OwnerSubjectId || caller.SessionId == Guid.Empty
            || request.OrganisationId != options.OrganisationId || request.OperationId == Guid.Empty)
            throw new WorkerRotationException("WORKER_ROTATION_OWNER_SCOPE_REJECTED");
        if (phase is not ("prepare" or "revoke-others"))
            throw new WorkerRotationException("WORKER_ROTATION_PHASE_REJECTED");
        if (phase == "revoke-others" && !ValidDeliveries(deliveries, options.RegistryId))
            throw new WorkerRotationException("WORKER_ROTATION_BOTH_VERIFIED_DELIVERIES_REQUIRED");
        return store.ExecuteAsync(phase, caller, request, deliveries, cancellationToken);
    }

    public static bool ValidDeliveries(IReadOnlyList<WorkerDeliveryReceipt>? deliveries, Guid registryId) => registryId != Guid.Empty
        && deliveries is { Count: 2 } && deliveries.All(value => value is not null)
        && WorkerProfiles.All.All(profile => deliveries.Count(value => value.Profile == profile) == 1)
        && deliveries.All(value => value.OperationId != Guid.Empty && value.Generation > 0 && value.SubjectId != Guid.Empty
            && value.SessionId != Guid.Empty && Digest(value.PayloadSha256) && Digest(value.PublicationSha256)
            && value.VolumeName == ExpectedVolume(value.Profile, registryId)
            && value.ConsumerService == (value.Profile == WorkerProfiles.AdminAreaRetry ? "adminarea" : "client")
            && value.DestinationPath == "/run/zeka-worker-credential/current.json"
            && value.OwnerUid == 1654 && value.OwnerGid == 1654 && value.DirectoryMode == "0500" && value.Mode == "0400"
            && value.ReadOnlyMount);

    public static string ExpectedVolume(string profile, Guid registryId) =>
        "zeka-m1-worker-" + (profile == WorkerProfiles.AdminAreaRetry ? "adminarea" : "client")
        + "-rotation-" + registryId.ToString("N")[..12];
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
