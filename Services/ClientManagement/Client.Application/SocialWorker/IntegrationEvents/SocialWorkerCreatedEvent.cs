using Zeka.Extensions.EventBus;

namespace ClientManagement.Application.SocialWorker.IntegrationEvents;

public record SocialWorkerCreatedEvent(string firstname, string lastname, string username, string teamname, string teamacronym, string tenant) : Event;