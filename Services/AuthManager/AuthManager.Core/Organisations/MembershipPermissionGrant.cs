using Zeka.Extensions.MultiTenancy.Abstractions;

namespace AuthManager.Core.Organisations;

/// <summary>
/// Append-preserving evidence that a permission was explicitly delegated to one organisation membership.
/// Revocation makes the grant ineffective without erasing its authority history.
/// </summary>
public sealed class MembershipPermissionGrant : ITenantOwnedEntity
{
    private MembershipPermissionGrant()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganisationId { get; private set; }

    public Guid OrganisationMembershipId { get; private set; }

    public string PermissionKey { get; private set; } = string.Empty;

    public Guid GrantedByMembershipId { get; private set; }

    public Guid GrantedBySubjectId { get; private set; }

    public DateTimeOffset GrantedAtUtc { get; private set; }

    public Guid? RevokedByMembershipId { get; private set; }

    public Guid? RevokedBySubjectId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public uint ConcurrencyVersion { get; private set; }

    public bool IsActive => RevokedAtUtc is null;

    public static MembershipPermissionGrant Create(
        Guid id,
        Guid organisationId,
        Guid organisationMembershipId,
        string permissionKey,
        Guid grantedByMembershipId,
        Guid grantedBySubjectId,
        DateTimeOffset grantedAtUtc)
    {
        EnsureNotEmpty(id, nameof(id));
        EnsureNotEmpty(organisationId, nameof(organisationId));
        EnsureNotEmpty(organisationMembershipId, nameof(organisationMembershipId));
        EnsureNotEmpty(grantedByMembershipId, nameof(grantedByMembershipId));
        EnsureNotEmpty(grantedBySubjectId, nameof(grantedBySubjectId));
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);

        return new MembershipPermissionGrant
        {
            Id = id,
            OrganisationId = organisationId,
            OrganisationMembershipId = organisationMembershipId,
            PermissionKey = permissionKey.Trim(),
            GrantedByMembershipId = grantedByMembershipId,
            GrantedBySubjectId = grantedBySubjectId,
            GrantedAtUtc = grantedAtUtc,
            ConcurrencyVersion = 1
        };
    }

    public bool Revoke(
        Guid revokedByMembershipId,
        Guid revokedBySubjectId,
        DateTimeOffset revokedAtUtc)
    {
        EnsureNotEmpty(revokedByMembershipId, nameof(revokedByMembershipId));
        EnsureNotEmpty(revokedBySubjectId, nameof(revokedBySubjectId));

        if (!IsActive || revokedAtUtc < GrantedAtUtc)
        {
            return false;
        }

        RevokedByMembershipId = revokedByMembershipId;
        RevokedBySubjectId = revokedBySubjectId;
        RevokedAtUtc = revokedAtUtc;
        ConcurrencyVersion++;
        return true;
    }

    private static void EnsureNotEmpty(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Identifier must not be empty.", parameterName);
        }
    }
}
