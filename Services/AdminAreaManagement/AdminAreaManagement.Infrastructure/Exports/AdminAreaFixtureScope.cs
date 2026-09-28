namespace AdminAreaManagement.Infrastructure.Exports;

/// <summary>Fail-closed marker restricting LIFE-01 export materialization to an evidence fixture tenant.</summary>
public sealed class AdminAreaFixtureScope(Guid organisationId)
{
    public Guid OrganisationId { get; } = organisationId != Guid.Empty
        ? organisationId : throw new ArgumentException("Fixture organisation is required.", nameof(organisationId));

    public void Demand(Guid requested)
    {
        if (requested != OrganisationId)
            throw new InvalidOperationException("AdminArea export is enabled only for the configured fixture organisation.");
    }
}
