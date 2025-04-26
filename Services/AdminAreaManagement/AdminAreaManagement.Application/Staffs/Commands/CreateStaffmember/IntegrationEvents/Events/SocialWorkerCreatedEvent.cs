using Zeka.Extensions.EventBus;

namespace AdminAreaManagement.Application.Staffs.Commands.CreateStaffmember.IntegrationEvents.Events;

public record SocialWorkerCreatedEvent(string firstname, string lastname, string username, string teamname, string teamacronym, string tenant) : Event;