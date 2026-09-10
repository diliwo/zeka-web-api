using ClientManagement.Core.Interfaces;
using Zeka.Extensions.EventBus.Abstractions;

namespace ClientManagement.Application.SocialWorker.IntegrationEvents.EventHandlers;

public class SocialWorkerCreatedEventHandler : IEventHandler<SocialWorkerCreatedEvent>
{
    private readonly IRepositoryManager _repository;
    public SocialWorkerCreatedEventHandler(IRepositoryManager repository)
    {
        _repository = repository;
    }

    public Task Handle(SocialWorkerCreatedEvent @event)
    {
        // Legacy messages contain a mutable name and no immutable membership or revision.
        // They require reviewed replay through the versioned contract, never name-based inference.
        throw new ClientManagement.Application.Common.Authorization.TenantAccessException(
            ClientManagement.Application.Common.Authorization.AccessFailure.Denied);
    }
}
