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
        _repository.SocialWorker.Persist(new Core.Entities.SocialWorker(@event.firstname, @event.lastname, @event.teamname, @event.teamacronym, @event.username));

        _repository.SaveAsync();

        return Task.CompletedTask;
    }
}