using Zeka.Extensions.EventBus;

namespace AdminAreaManagement.Application.Staffs.Commands.UpsertStaff.IntegrationEvents.Events;

public record StaffMemberUpsertEvent(string name) : Event;