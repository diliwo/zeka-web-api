namespace AdminAreaManagement.Application.Common.Authorization;

/// <summary>
/// Fails closed when ordinary tenant activity is attempted after a durable AdminArea closure boundary.
/// Lifecycle recovery uses its dedicated participant/store path and never a caller-controlled bypass.
/// </summary>
public interface IAdminAreaClosureGate
{
    Task DemandOrdinaryAccessAsync(CancellationToken cancellationToken);
}
