using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IDomainEventService
    {
        Task Publish(DomainEvent domainEvent);
    }
}
