using System.Collections.Concurrent;
using Zeka.PersistenceSecurity;

namespace Zeka.PersistenceSecurity.Tests;

public sealed class TenantAttemptEvidenceCollector : ITenantAttemptOrderObserver
{
    private readonly ConcurrentQueue<TenantAttemptOrderEvent> events = new();

    public void Observe(TenantAttemptOrderEvent evidence) => events.Enqueue(evidence);

    public TenantAttemptOrderEvent[] Snapshot() => events.ToArray();

    public void Clear()
    {
        while (events.TryDequeue(out _)) { }
    }
}
