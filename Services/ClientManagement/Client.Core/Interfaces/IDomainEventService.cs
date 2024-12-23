using ClientManagement.Core.Common;

namespace ClientManagement.Core.Interfaces
{
    public interface IDomainEventService
    {
        Task Publish(DomainEvent domainEvent);
    }
}
