using ClientManagement.Core.Common;

namespace ClientManagement.Core.Lifecycle;

public enum OrganisationExportFenceState
{
    Active = 1,
    Released = 2
}

public sealed class OrganisationExportFence : TenantOwnedEntity
{
    private OrganisationExportFence() { }

    public Guid OperationId { get; private set; }
    public long OperationRevision { get; private set; }
    public string ParticipantId { get; private set; } = string.Empty;
    public string FenceToken { get; private set; } = string.Empty;
    public long FenceRevision { get; private set; } = 1;
    public OrganisationExportFenceState State { get; private set; }
    public DateTimeOffset EnteredAt { get; private set; }
    public DateTimeOffset? ReleasedAt { get; private set; }

    public static OrganisationExportFence Enter(Guid operationId, Guid organisationId, long operationRevision,
        string participantId, string fenceToken, DateTimeOffset enteredAt)
    {
        if (operationId == Guid.Empty || organisationId == Guid.Empty || operationRevision <= 0
            || string.IsNullOrWhiteSpace(participantId) || string.IsNullOrWhiteSpace(fenceToken)
            || enteredAt == default || enteredAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Export fence identity is invalid.");
        var fence = new OrganisationExportFence
        {
            OperationId = operationId,
            OperationRevision = operationRevision,
            ParticipantId = participantId,
            FenceToken = fenceToken,
            State = OrganisationExportFenceState.Active,
            EnteredAt = enteredAt
        };
        fence.AssignToOrganisation(organisationId);
        return fence;
    }

    public void Release(string fenceToken, DateTimeOffset releasedAt)
    {
        if (!StringComparer.Ordinal.Equals(FenceToken, fenceToken))
            throw new InvalidOperationException("Export fence token does not match.");
        if (releasedAt == default || releasedAt.Offset != TimeSpan.Zero || releasedAt < EnteredAt)
            throw new InvalidOperationException("Export fence release time is invalid.");
        if (State == OrganisationExportFenceState.Released) return;
        State = OrganisationExportFenceState.Released;
        ReleasedAt = releasedAt;
        FenceRevision = checked(FenceRevision + 1);
    }
}
