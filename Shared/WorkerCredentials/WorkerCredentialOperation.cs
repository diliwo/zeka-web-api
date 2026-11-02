using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Zeka.WorkerCredentials;

// Opt-in capability carried only by one worker operation, never a shared HTTP handler or user identity.
public interface IWorkerCredential401Retry
{
    ValueTask<bool> ReloadAfterUnauthorizedAsync(CancellationToken cancellationToken);
}

public sealed class WorkerCredentialOperation
{
    private readonly IWorkerCredentialProvider provider;
    private WorkerCredentialSnapshot snapshot;
    private int reloadAttempted;

    public WorkerCredentialOperation(IWorkerCredentialProvider provider, WorkerCredentialSnapshot snapshot,
        Guid organisationId)
    {
        if (organisationId == Guid.Empty || !snapshot.OrganisationIds.Contains(organisationId))
            throw new WorkerCredentialUnavailableException("WORKER_SCOPE_REJECTED");
        this.provider = provider;
        this.snapshot = snapshot;
    }

    public WorkerCredentialSnapshot Snapshot => Volatile.Read(ref snapshot);

    public async ValueTask<bool> ReloadAfterUnauthorizedAsync(CancellationToken cancellationToken)
    {
        // One shared reload budget across all Auth queries in this operation. A failed read consumes it.
        if (Interlocked.Exchange(ref reloadAttempted, 1) != 0) return false;
        var previous = Snapshot;
        var next = await provider.ReadAsync(cancellationToken);
        if (next.SubjectId != previous.SubjectId || next.Profile != previous.Profile
            || !next.OrganisationIds.OrderBy(x => x).SequenceEqual(previous.OrganisationIds.OrderBy(x => x))
            || next.Generation < previous.Generation
            || (next.Generation == previous.Generation && next.PayloadSha256 != previous.PayloadSha256))
            throw new WorkerCredentialUnavailableException("WORKER_RELOAD_CONTEXT_REJECTED");
        Volatile.Write(ref snapshot, next);
        return true;
    }

    public void MarkSuccessfulUse() => provider.MarkSuccessfulUse(Snapshot);
}
