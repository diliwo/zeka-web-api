using ClientManagement.Core.Common;

namespace ClientManagement.Core.Lifecycle;

public enum OrganisationClosureFenceState
{
    Active = 1,
    Released = 2
}

public sealed class OrganisationClosureFence : TenantOwnedEntity
{
    private OrganisationClosureFence() { }

    public Guid OperationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public string FenceToken { get; private set; } = string.Empty;
    public long FenceRevision { get; private set; } = 1;
    public OrganisationClosureFenceState State => ReleasedAt is null
        ? OrganisationClosureFenceState.Active
        : OrganisationClosureFenceState.Released;
    public DateTimeOffset EnteredAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    public static OrganisationClosureFence Enter(Guid operationId, Guid organisationId, long operationRevision,
        string participantId, string requestHash, string fenceToken, DateTimeOffset enteredAt)
    {
        if (operationId == Guid.Empty || organisationId == Guid.Empty || operationRevision <= 0
            || string.IsNullOrWhiteSpace(participantId) || string.IsNullOrWhiteSpace(requestHash)
            || string.IsNullOrWhiteSpace(fenceToken) || enteredAt == default || enteredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Closure fence identity is invalid.");
        var fence = new OrganisationClosureFence
        {
            OperationId = operationId,
            OperationRevision = operationRevision,
            ParticipantId = participantId,
            RequestHash = requestHash,
            FenceToken = fenceToken,
            EnteredAt = enteredAt
        };
        fence.AssignToOrganisation(organisationId);
        return fence;
    }

    public void Release(string fenceToken, DateTimeOffset releasedAt)
    {
        if (!StringComparer.Ordinal.Equals(FenceToken, fenceToken))
            throw new InvalidOperationException("Closure fence token does not match.");
        if (releasedAt == default || releasedAt.Offset != TimeSpan.Zero || releasedAt < EnteredAt)
            throw new InvalidOperationException("Closure fence release time is invalid.");
        if (State == OrganisationClosureFenceState.Released) return;
        ReleasedAt = releasedAt;
    }
}
