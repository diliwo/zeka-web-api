namespace ClientManagement.Application.Common.Authorization;

/// <summary>Serializes all SocialCase writers with dossier action writers inside a tenant transaction.</summary>
public interface IClientParentLock
{
    Task LockAsync(int clientId, CancellationToken cancellationToken);
    Task<int?> ResolveCaseClientAsync(int socialCaseId, CancellationToken cancellationToken);
    Task<int?> ResolveActionClientAsync(int actionId, CancellationToken cancellationToken);
    Task LockWorkerAsync(int socialWorkerId, CancellationToken cancellationToken);
    Task LockActionAsync(int actionId, CancellationToken cancellationToken);
}
