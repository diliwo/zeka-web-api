using ClientManagement.Core.Interfaces;
using Tenant;
using Zeka.Extensions.EventBus.Abstractions;

namespace ClientManagement.Application.SocialWorker.IntegrationEvents.EventHandlers;

public class SocialWorkerCreatedEventHandler : IEventHandler<SocialWorkerCreatedEvent>
{
    private readonly IRepositoryManager _repository;
    public SocialWorkerCreatedEventHandler(IRepositoryManager repository, ITenantService service)
    {
        _repository = repository;
    }

    public Task Handle(SocialWorkerCreatedEvent @event)
    {
        var newSocialWorker = new Core.Entities.SocialWorker(@event.firstname, @event.lastname, @event.teamname,
            @event.teamacronym, @event.username);
        newSocialWorker.TenantName = @event.tenant; // We retrieve the tenant id from the message

        _repository.SocialWorker.Persist(newSocialWorker);

        _repository.Save();

        return Task.CompletedTask;
    }
}