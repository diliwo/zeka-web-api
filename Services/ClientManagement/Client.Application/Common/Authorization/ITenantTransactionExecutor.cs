namespace ClientManagement.Application.Common.Authorization;

/// <summary>Owns one complete, retryable database transaction attempt for an authorized tenant operation.</summary>
public interface ITenantTransactionExecutor
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
    Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
    Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}
