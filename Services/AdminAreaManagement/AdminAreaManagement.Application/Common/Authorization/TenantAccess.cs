using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AdminAreaManagement.Application.Common.Authorization;

public enum TenantAccessOutcome { Authorized, Unauthenticated, Denied, Unavailable }

public sealed record AuthorizedTenantMembership(Guid OrganisationId, Guid OrganisationMembershipId,
    IReadOnlyCollection<string> EffectivePermissionCodes, string DecisionVersion, DateTimeOffset ObservedAtUtc);

public sealed record TenantAccessDecision(TenantAccessOutcome Outcome, AuthorizedTenantMembership? Membership = null,
    string? Reason = null);

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
public sealed class TenantAccessException(AccessFailure failure, string? reason = null) : Exception("Tenant access rejected.")
{
    public AccessFailure Failure { get; } = failure;
    public string? Reason { get; } = reason;
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RequiresTenantPermissionAttribute(string permission, string? assignedAlternative = null) : Attribute
{
    public string Permission { get; } = permission;
    public string? AssignedAlternative { get; } = assignedAlternative;
}

public sealed class TenantOperation(ICurrentTenantAccess access, IOperationIdentity identity,
    ITenantContextInitializer initializer, ITenantContextAccessor context, ILogger<TenantOperation>? logger = null)
{
    private bool established;
    public Guid MembershipId { get; private set; }
    public bool AssignedOnly { get; private set; }
    public string Permission { get; private set; } = string.Empty;

    public async Task AuthorizeAsync(RequiresTenantPermissionAttribute policy, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identity.SubjectId))
            throw new TenantAccessException(AccessFailure.Unauthenticated);
        if (identity.SelectedOrganisationId == Guid.Empty)
            throw new TenantAccessException(AccessFailure.Denied);
        if (!PermissionCatalogue.IsKnown(policy.Permission)
            || (policy.AssignedAlternative is not null && !PermissionCatalogue.IsKnown(policy.AssignedAlternative)))
        {
            TenantAccessDiagnostics.UnknownCheckedPermission();
            throw new TenantAccessException(AccessFailure.Denied, TenantAccessDiagnostics.PermissionCheckUnknown);
        }

        var decision = await access.ResolveAsync(identity.SubjectId, identity.SelectedOrganisationId, cancellationToken);
        if (decision.Outcome != TenantAccessOutcome.Authorized && decision.Membership is not null)
        {
            TenantAccessDiagnostics.Integrity("contradictory_response", logger);
            throw new TenantAccessException(AccessFailure.Unavailable, TenantAccessDiagnostics.IntegrityMismatch);
        }
        var reason = TenantAccessDiagnostics.KnownReason(decision.Reason);
        if (decision.Outcome == TenantAccessOutcome.Unauthenticated)
            throw new TenantAccessException(AccessFailure.Unauthenticated, reason);
        if (decision.Outcome == TenantAccessOutcome.Unavailable)
            throw new TenantAccessException(AccessFailure.Unavailable, reason ?? TenantAccessDiagnostics.DependencyUnavailable);
        var grant = decision.Membership;
        if (decision.Outcome == TenantAccessOutcome.Denied && grant is null)
            throw new TenantAccessException(AccessFailure.Denied, reason);
        if (decision.Outcome != TenantAccessOutcome.Authorized || grant is null
            || grant.OrganisationId != identity.SelectedOrganisationId || grant.OrganisationMembershipId == Guid.Empty)
        {
            TenantAccessDiagnostics.Integrity(grant is not null && grant.OrganisationId != identity.SelectedOrganisationId
                ? "organisation_mismatch" : "contradictory_response", logger);
            throw new TenantAccessException(AccessFailure.Unavailable, TenantAccessDiagnostics.IntegrityMismatch);
        }
        if (!TenantAccessDiagnostics.ValidPermissions(grant.EffectivePermissionCodes)
            || string.IsNullOrWhiteSpace(grant.DecisionVersion) || grant.ObservedAtUtc == default
            || grant.ObservedAtUtc.Offset != TimeSpan.Zero)
            throw new TenantAccessException(AccessFailure.Unavailable, TenantAccessDiagnostics.ResponseMalformed);
        var permissions = TenantAccessDiagnostics.RecognizedPermissions(grant.EffectivePermissionCodes).ToFrozenSet(StringComparer.Ordinal);
        var all = permissions.Contains(policy.Permission);
        if (!all && (policy.AssignedAlternative is null || !permissions.Contains(policy.AssignedAlternative)))
            throw new TenantAccessException(AccessFailure.Denied);

        if (established)
        {
            // A scope is one operation. Re-entry must not change either identity or resource permissions.
            if (context.Current.OrganisationId.Value != grant.OrganisationId || context.Current.SubjectId != identity.SubjectId
                || MembershipId != grant.OrganisationMembershipId || Permission != policy.Permission || AssignedOnly != !all)
            {
                TenantAccessDiagnostics.Integrity("contradictory_response", logger);
                throw new TenantAccessException(AccessFailure.Unavailable, TenantAccessDiagnostics.IntegrityMismatch);
            }
        }
        else
        {
            initializer.Establish(new TenantContext(new TenantId(grant.OrganisationId), identity.SubjectId));
            MembershipId = grant.OrganisationMembershipId;
            Permission = policy.Permission;
            AssignedOnly = !all;
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
