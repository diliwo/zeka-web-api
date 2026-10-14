using System.Collections.Frozen;
using ClientManagement.Application.ActionWrites;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Application.Common.Authorization;

public enum TenantAccessOutcome { Authorized, Unauthenticated, Denied, Unavailable }

public sealed record AuthorizedTenantMembership(Guid OrganisationId, Guid OrganisationMembershipId,
    IReadOnlyCollection<string> EffectivePermissionCodes, string DecisionVersion, DateTimeOffset ObservedAtUtc);

public sealed record TenantAccessDecision(TenantAccessOutcome Outcome, AuthorizedTenantMembership? Membership = null);

public interface ICurrentTenantAccess
{
    Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
        CancellationToken cancellationToken);
}

// Implementations receive authenticated identity from the delivery boundary. Selection remains untrusted.
public interface IOperationIdentity
{
    string SubjectId { get; }
    Guid SelectedOrganisationId { get; }
}

public enum AccessFailure { Unauthenticated, Denied, Unavailable }
public sealed class TenantAccessException(AccessFailure failure) : Exception("Tenant access rejected.")
{
    public AccessFailure Failure { get; } = failure;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresTenantPermissionAttribute(string permission, string? assignedAlternative = null) : Attribute
{
    public string Permission { get; } = permission;
    public string? AssignedAlternative { get; } = assignedAlternative;
}

public sealed class TenantOperation(ICurrentTenantAccess access, IOperationIdentity identity,
    ITenantContextInitializer initializer, ITenantContextAccessor context, ActionWriteAuthorityScope? actionScope = null)
{
    private bool established;
    public Guid MembershipId { get; private set; }
    public bool AssignedOnly { get; private set; }
    public bool CanEditAll { get; private set; }
    public bool CanEditAssigned { get; private set; }
    public string Permission { get; private set; } = string.Empty;
    public string SubjectId => identity.SubjectId;
    public Guid OrganisationId => identity.SelectedOrganisationId;
    public DateOnly? ActionLocalDay => actionScope?.LocalDay;

    public Task AuthorizeAsync(RequiresTenantPermissionAttribute policy, CancellationToken cancellationToken) =>
        AuthorizeCoreAsync(policy.Permission, policy.AssignedAlternative, null, null, cancellationToken);

    // B1 read-only surface accepts either current View or Edit at the same dossier scope.
    // Legacy request policies retain their exact two-code semantics.
    public Task AuthorizeBeneficiaryReadAsync(CancellationToken cancellationToken) =>
        AuthorizeCoreAsync("Clients.ViewAll", "Clients.ViewAssigned", "Clients.EditAll",
            "Clients.EditAssigned", cancellationToken);

    private async Task AuthorizeCoreAsync(string allPermission, string? assignedPermission,
        string? secondAllPermission, string? secondAssignedPermission, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.SubjectId))
            throw new TenantAccessException(AccessFailure.Unauthenticated);
        if (identity.SelectedOrganisationId == Guid.Empty || !PermissionCatalogue.IsKnown(allPermission)
            || (assignedPermission is not null && !PermissionCatalogue.IsKnown(assignedPermission))
            || (secondAllPermission is not null && !PermissionCatalogue.IsKnown(secondAllPermission))
            || (secondAssignedPermission is not null && !PermissionCatalogue.IsKnown(secondAssignedPermission)))
            throw new TenantAccessException(AccessFailure.Denied);

        var decision = await access.ResolveAsync(identity.SubjectId, identity.SelectedOrganisationId, cancellationToken);
        if (decision.Outcome == TenantAccessOutcome.Unauthenticated)
            throw new TenantAccessException(AccessFailure.Unauthenticated);
        if (decision.Outcome == TenantAccessOutcome.Unavailable)
            throw new TenantAccessException(AccessFailure.Unavailable);
        var grant = decision.Membership;
        if (decision.Outcome != TenantAccessOutcome.Authorized || grant is null
            || grant.OrganisationId != identity.SelectedOrganisationId || grant.OrganisationMembershipId == Guid.Empty
            || grant.EffectivePermissionCodes is null || string.IsNullOrWhiteSpace(grant.DecisionVersion)
            || grant.EffectivePermissionCodes.Any(permission => !PermissionCatalogue.IsKnown(permission)))
            throw new TenantAccessException(AccessFailure.Denied);
        var permissions = grant.EffectivePermissionCodes.ToFrozenSet(StringComparer.Ordinal);
        var all = permissions.Contains(allPermission)
            || secondAllPermission is not null && permissions.Contains(secondAllPermission);
        if (!all && (assignedPermission is null || !permissions.Contains(assignedPermission))
            && (secondAssignedPermission is null || !permissions.Contains(secondAssignedPermission)))
            throw new TenantAccessException(AccessFailure.Denied);

        if (established)
        {
            // A scope is one operation. Re-entry must not change either identity or resource permissions.
            if (context.Current.OrganisationId.Value != grant.OrganisationId || context.Current.SubjectId != identity.SubjectId
                || MembershipId != grant.OrganisationMembershipId || Permission != allPermission || AssignedOnly != !all)
                throw new TenantAccessException(AccessFailure.Denied);
        }
        else
        {
            initializer.Establish(new TenantContext(new TenantId(grant.OrganisationId), identity.SubjectId));
            MembershipId = grant.OrganisationMembershipId;
            Permission = allPermission;
            AssignedOnly = !all;
            CanEditAll = permissions.Contains("Clients.EditAll");
            CanEditAssigned = permissions.Contains("Clients.EditAssigned");
            established = true;
        }
    }
}

public static class PermissionCatalogue
{
    private static readonly FrozenSet<string> Known = new[]
    {
        "Clients.ViewAssigned", "Clients.ViewAll", "Clients.Create", "Clients.EditAssigned",
        "Clients.EditAll", "Clients.Delete", "Clients.ImportExport", "TeamConfiguration.View",
        "TeamConfiguration.ManageTeams", "TeamConfiguration.ManageStaffProfiles",
        "TeamConfiguration.ManageMemberships", "Partners.View", "Partners.Manage", "Partners.Delete",
        "PartnerDocuments.View", "PartnerDocuments.Import", "PartnerDocuments.Delete", "ReferenceData.View"
    }.ToFrozenSet(StringComparer.Ordinal);
    public static bool IsKnown(string permission) => Known.Contains(permission);
}
