namespace AuthManager.Core.Organisations;

public sealed class OrganisationMembership
{
    private OrganisationMembership()
    {
    }

    public Guid Id { get; private set; }

    public Guid OrganisationId { get; private set; }

    public Guid UserId { get; private set; }

    public Guid PermissionSetId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset JoinedAtUtc { get; private set; }

    public DateTimeOffset? SuspendedAtUtc { get; private set; }

    public DateTimeOffset? EndedAtUtc { get; private set; }

    public uint ConcurrencyVersion { get; private set; }

    public static OrganisationMembership Create(
        Guid id,
        Guid organisationId,
        Guid userId,
        Guid permissionSetId,
        DateTimeOffset joinedAtUtc)
    {
        EnsureNotEmpty(id, nameof(id));
        EnsureNotEmpty(organisationId, nameof(organisationId));
        EnsureNotEmpty(userId, nameof(userId));
        EnsureNotEmpty(permissionSetId, nameof(permissionSetId));

        return new OrganisationMembership
        {
            Id = id,
            OrganisationId = organisationId,
            UserId = userId,
            PermissionSetId = permissionSetId,
            Status = MembershipStatus.Active,
            JoinedAtUtc = joinedAtUtc,
            ConcurrencyVersion = 1
        };
    }

    public static OrganisationMembership CreateOwner(
        Guid id,
        Guid organisationId,
        Guid ownerUserId,
        DateTimeOffset joinedAtUtc) =>
        Create(id, organisationId, ownerUserId, PermissionSet.OrganisationOwnerId, joinedAtUtc);

    public bool Suspend(DateTimeOffset suspendedAtUtc)
    {
        if (Status != MembershipStatus.Active || suspendedAtUtc < JoinedAtUtc)
        {
            return false;
        }

        Status = MembershipStatus.Suspended;
        SuspendedAtUtc = suspendedAtUtc;
        ConcurrencyVersion++;
        return true;
    }

    public bool Reactivate(DateTimeOffset reactivatedAtUtc)
    {
        if (Status != MembershipStatus.Suspended || reactivatedAtUtc < SuspendedAtUtc)
        {
            return false;
        }

        Status = MembershipStatus.Active;
        SuspendedAtUtc = null;
        ConcurrencyVersion++;
        return true;
    }

    public bool Leave(DateTimeOffset endedAtUtc)
    {
        var lastTransitionAtUtc = SuspendedAtUtc ?? JoinedAtUtc;
        if (Status == MembershipStatus.Left || endedAtUtc < lastTransitionAtUtc)
        {
            return false;
        }

        Status = MembershipStatus.Left;
        EndedAtUtc = endedAtUtc;
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
