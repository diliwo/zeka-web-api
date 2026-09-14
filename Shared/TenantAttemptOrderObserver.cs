namespace Zeka.PersistenceSecurity;

public enum TenantAttemptCommandCategory
{
    Begin,
    ContextInitialized,
    EfRead,
    EfWrite,
    EfRawSql,
    Commit,
    Rollback,
    RejectedBeforeDispatch
}

public sealed record TenantAttemptOrderEvent(
    Guid? AttemptId,
    Guid? TransactionId,
    int? BackendProcessId,
    long Sequence,
    TenantAttemptCommandCategory Category);

public interface ITenantAttemptOrderObserver
{
    void Observe(TenantAttemptOrderEvent evidence);
}

public sealed class NullTenantAttemptOrderObserver : ITenantAttemptOrderObserver
{
    public void Observe(TenantAttemptOrderEvent evidence) { }
}
