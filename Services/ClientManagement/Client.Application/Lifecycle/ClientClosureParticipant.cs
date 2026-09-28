using Zeka.Lifecycle.Contracts;

namespace ClientManagement.Application.Lifecycle;

public static class ClientClosureContract
{
    public const string ParticipantId = "client-management";
    public const string Capability = OrganisationClosureCapabilityV1.Fence;
    public const string OwnershipScope = "client-management-owned-records";
}

public interface IClientClosureFixtureScope
{
    void Demand(Guid organisationId);
}

public interface IClientOrganisationClosureParticipant
{
    Task<OrganisationClosureParticipantCompletedV1> EnterFenceAsync(
        CloseOrganisationParticipantV1 command,
        CancellationToken cancellationToken = default);

    Task<OrganisationClosureFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationClosureFenceV1 command,
        CancellationToken cancellationToken = default);
}
