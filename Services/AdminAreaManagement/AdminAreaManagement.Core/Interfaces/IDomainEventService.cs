using AdminAreaManagement.Core.Commun;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface IDomainEventService
    {
        Task Publish(DomainEvent domainEvent);
    }
}
