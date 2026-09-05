namespace AdminAreaManagement.Core.Common
{
    public abstract class AggregateRoot : TenantOwnedEntity
    {
        private readonly List<IHasDomainEvent> _domainEvents = new List<IHasDomainEvent>();
        public virtual IReadOnlyList<IHasDomainEvent> DomainEvents => _domainEvents;

        protected virtual void AddDomainEvent(IHasDomainEvent newEvent)
        {
            _domainEvents.Add(newEvent);
        }

        public virtual void ClearEvents()
        {
            _domainEvents.Clear();
        }
    }
}
