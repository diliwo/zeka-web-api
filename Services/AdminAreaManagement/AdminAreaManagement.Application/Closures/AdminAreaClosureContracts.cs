using Zeka.Lifecycle.Contracts;

namespace AdminAreaManagement.Application.Closures;

public static class AdminAreaClosureContractV1
{
    public const string ParticipantId = "admin-area";
    public const string DocumentParticipantId = "admin-area-documents";
    public const string Capability = OrganisationClosureCapabilityV1.Fence;

    public static bool Owns(string participantId) => participantId is ParticipantId or DocumentParticipantId;
}

public interface IAdminAreaClosureParticipant
{
    Task<OrganisationClosureParticipantCompletedV1> EnterFenceAsync(
        CloseOrganisationParticipantV1 command, CancellationToken cancellationToken = default);

    Task<OrganisationClosureFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationClosureFenceV1 command, CancellationToken cancellationToken = default);
}
