using AuthManager.Application.Authorization;
using AuthManager.Core.Lifecycle;

namespace AuthManager.Application.Lifecycle;

public static class LifecyclePermissions
{
    public const string Export = "Organisations.Export";
    public const string Close = "Organisations.Close";
}

public enum AdmissionStatus { Admitted, Replay, Conflict, Denied, Unavailable, RegistryUnavailable }
public sealed record AdmissionResult(AdmissionStatus Status, LifecycleOperation? Operation = null);

/// <summary>Created only after a current membership/permission observation.</summary>
public sealed class AuthorizedLifecycleAdmission
{
    internal AuthorizedLifecycleAdmission(Guid subject, Guid organisation, LifecycleOperationFamily family,
        Guid idempotencyId)
    { SubjectId = subject; OrganisationId = organisation; Family = family; IdempotencyId = idempotencyId; }
    public Guid SubjectId { get; }
    public Guid OrganisationId { get; }
    public LifecycleOperationFamily Family { get; }
    public Guid IdempotencyId { get; }
}

public interface ILifecycleAdmissionStore
{
    Task<AdmissionResult> AdmitAsync(AuthorizedLifecycleAdmission admission, CancellationToken cancellationToken);
}

public interface ILifecycleRegistryReader
{
    Task<LifecycleRegistry?> ReadActiveAsync(CancellationToken cancellationToken);
}

/// <summary>Governance/test port only. Must never be registered in production runtime DI.</summary>
public interface IReviewedLifecycleRegistryActivation
{
    Task<bool> ActivateAsync(LifecycleRegistry registry, long expectedVersion, CancellationToken cancellationToken);
}

public sealed class LifecycleAdmission(ICurrentTenantAccess access, ILifecycleAdmissionStore store)
{
    public async Task<AdmissionResult> AdmitAsync(Guid authenticatedSubjectId, Guid organisationId,
        LifecycleOperationFamily family, Guid idempotencyId, CancellationToken cancellationToken = default)
    {
        if (authenticatedSubjectId == Guid.Empty || organisationId == Guid.Empty
            || idempotencyId == Guid.Empty || !Enum.IsDefined(family))
            return new(AdmissionStatus.Denied);
        var current = await access.ResolveAsync(authenticatedSubjectId, organisationId, cancellationToken);
        if (current.Outcome == TenantAccessOutcome.Unavailable) return new(AdmissionStatus.Unavailable);
        var permission = family == LifecycleOperationFamily.Export ? LifecyclePermissions.Export : LifecyclePermissions.Close;
        if (current.Outcome != TenantAccessOutcome.Authorized || current.OrganisationId != organisationId
            || current.OrganisationMembershipId == Guid.Empty
            || !current.EffectivePermissionCodes.Contains(permission, StringComparer.Ordinal))
            return new(AdmissionStatus.Denied);
        return await store.AdmitAsync(new(authenticatedSubjectId, organisationId, family, idempotencyId), cancellationToken);
    }
}
