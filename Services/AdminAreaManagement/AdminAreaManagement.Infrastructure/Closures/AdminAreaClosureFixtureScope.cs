namespace AdminAreaManagement.Infrastructure.Closures;

/// <summary>Fail-closed marker restricting LIFE-02 deterministic dispatch to an evidence fixture tenant.</summary>
public sealed class AdminAreaClosureFixtureScope(Guid organisationId)
{
    public Guid OrganisationId { get; } = organisationId != Guid.Empty
        ? organisationId : throw new ArgumentException("Fixture organisation is required.", nameof(organisationId));

    public void Demand(Guid requested)
    {
        if (requested != OrganisationId)
            throw new InvalidOperationException(
                "AdminArea closure is enabled only for the configured fixture organisation.");
    }
}
