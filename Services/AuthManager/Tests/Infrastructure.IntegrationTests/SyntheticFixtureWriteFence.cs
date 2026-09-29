using AuthManager.Application.Lifecycle;

namespace Infrastructure.IntegrationTests;

/// <summary>Test-owned one-operation gate shared by ordinary fixture writers and terminal observation.</summary>
internal sealed class SyntheticFixtureWriteFence(Guid organisationId, Guid operationId)
    : INonProductionFixtureWriteFence
{
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private readonly Guid scopedOrganisationId = organisationId;
    private readonly Guid scopedOperationId = operationId;
    private Lease? activeFinalizationLease;
    private bool sealedForTerminal;

    public async Task<INonProductionFixtureWriteFenceLease> HoldAsync(Guid requestedOrganisationId,
        Guid requestedOperationId, CancellationToken cancellationToken)
    {
        if (requestedOrganisationId != scopedOrganisationId
            || requestedOperationId != scopedOperationId)
            throw new InvalidOperationException("Fixture write fence operation identity differs.");
        await semaphore.WaitAsync(cancellationToken);
        var lease = new Lease(this, true);
        activeFinalizationLease = lease;
        return lease;
    }

    public void DemandHeld(INonProductionFixtureWriteFenceLease lease,
        Guid requestedOrganisationId, Guid requestedOperationId)
    {
        if (!ReferenceEquals(lease, activeFinalizationLease)
            || !lease.IsHeldFor(requestedOrganisationId, requestedOperationId))
            throw new InvalidOperationException("Finalization requires the bound owner write fence.");
    }

    public async Task WriteAsync(Func<Task> write, CancellationToken cancellationToken = default)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (sealedForTerminal)
                throw new InvalidOperationException("Fixture writes are fenced after terminal verification.");
            await write();
        }
        finally { semaphore.Release(); }
    }

    public async Task<IAsyncDisposable> HoldWriteAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        if (sealedForTerminal)
        {
            semaphore.Release();
            throw new InvalidOperationException("Fixture writes are fenced after terminal verification.");
        }
        return new Lease(this, false);
    }

    private sealed class Lease(SyntheticFixtureWriteFence owner, bool finalization)
        : INonProductionFixtureWriteFenceLease
    {
        private bool released;
        public bool IsHeldFor(Guid requestedOrganisationId, Guid requestedOperationId) =>
            finalization && !released && requestedOrganisationId == owner.scopedOrganisationId
            && requestedOperationId == owner.scopedOperationId;
        public void Seal()
        {
            if (!finalization || released)
                throw new InvalidOperationException("Only a held finalization lease can seal writes.");
            owner.sealedForTerminal = true;
        }
        public ValueTask DisposeAsync()
        {
            if (!released)
            {
                released = true;
                if (finalization) owner.activeFinalizationLease = null;
                owner.semaphore.Release();
            }
            return ValueTask.CompletedTask;
        }
    }
}
