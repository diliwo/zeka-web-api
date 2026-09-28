using AuthManager.Core.Organisations;

namespace AuthManager.Application.Authorization;

public enum MembershipPermissionGrantStatus
{
    Granted,
    AlreadyGranted,
    Revoked,
    AlreadyRevoked,
    Denied,
    InvalidPermission,
    Unavailable
}

public sealed record MembershipPermissionGrantResult(
    MembershipPermissionGrantStatus Status,
    Guid? GrantId = null);

public sealed record MembershipPermissionGrantRequest(
    Guid ActorSubjectId,
    Guid ActorMembershipId,
    Guid OrganisationId,
    Guid TargetMembershipId,
    string PermissionKey,
    string CorrelationId);

public interface IMembershipPermissionGrantStore
{
    Task<MembershipPermissionGrantResult> GrantAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken);

    Task<MembershipPermissionGrantResult> RevokeAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Generic, non-delivery-specific application boundary for explicit membership permission grants.
/// Provider-real authority, tenancy and concurrency checks are owned by the persistence adapter.
/// </summary>
public sealed class MembershipPermissionGrants(IMembershipPermissionGrantStore store)
{
    public Task<MembershipPermissionGrantResult> GrantAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, store.GrantAsync, cancellationToken);

    public Task<MembershipPermissionGrantResult> RevokeAsync(
        MembershipPermissionGrantRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, store.RevokeAsync, cancellationToken);

    private static Task<MembershipPermissionGrantResult> ExecuteAsync(
        MembershipPermissionGrantRequest request,
        Func<MembershipPermissionGrantRequest, CancellationToken, Task<MembershipPermissionGrantResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ActorSubjectId == Guid.Empty
            || request.ActorMembershipId == Guid.Empty
            || request.OrganisationId == Guid.Empty
            || request.TargetMembershipId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.CorrelationId)
            || request.CorrelationId.Length > 200)
        {
            return Task.FromResult(new MembershipPermissionGrantResult(
                MembershipPermissionGrantStatus.Denied));
        }

        if (string.IsNullOrWhiteSpace(request.PermissionKey)
            || !TenantPermissions.IsKnown(request.PermissionKey.Trim()))
        {
            return Task.FromResult(new MembershipPermissionGrantResult(
                MembershipPermissionGrantStatus.InvalidPermission));
        }

        return operation(request with { PermissionKey = request.PermissionKey.Trim() }, cancellationToken);
    }
}
