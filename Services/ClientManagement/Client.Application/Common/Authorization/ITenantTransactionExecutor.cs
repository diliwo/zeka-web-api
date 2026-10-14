namespace ClientManagement.Application.Common.Authorization;

public sealed class TenantCommitIndeterminateException(Exception innerException)
    : Exception("The tenant transaction commit outcome is indeterminate and was not replayed.", innerException);

/// <summary>Owns one complete, retryable database transaction attempt for an authorized tenant operation.</summary>
public interface ITenantTransactionExecutor
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
    Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
    Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task ExecuteLifecycleAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
    Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task<T> ExecuteOrdinaryOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
