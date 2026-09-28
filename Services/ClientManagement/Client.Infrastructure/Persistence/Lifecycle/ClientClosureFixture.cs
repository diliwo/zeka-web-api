using ClientManagement.Application.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClientManagement.Infrastructure.Persistence.Lifecycle;

public sealed class ClientClosureFixtureScope(Guid organisationId) : IClientClosureFixtureScope
{
    public Guid OrganisationId { get; } = organisationId != Guid.Empty
        ? organisationId
        : throw new ArgumentException("A fixture organisation is required.", nameof(organisationId));

    public void Demand(Guid requestedOrganisationId)
    {
        if (requestedOrganisationId != OrganisationId)
            throw new InvalidOperationException("client_closure_nonfixture_scope_rejected");
    }
}

public static class ClientClosureFixtureRegistration
{
    public static IServiceCollection AddClientClosureFixtureParticipant(
        this IServiceCollection services,
        Guid fixtureOrganisationId)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClientClosureFixtureScope>(new ClientClosureFixtureScope(fixtureOrganisationId));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IClientOrganisationClosureParticipant, ClientOrganisationClosureParticipant>();
        return services;
    }
}
