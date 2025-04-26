using Zeka.Extensions.EventBus;

namespace AdminAreaManagement.Application.Staffs.Commands.UpdateStaffmember.IntegrationEvents.Events;

public record StaffMemberUpdateEvent(string firstname, string lastname, string username, string teamname, string teamacronym) : Event;