using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface IDomainEventService
    {
        Task Publish(DomainEvent domainEvent);
    }
}
