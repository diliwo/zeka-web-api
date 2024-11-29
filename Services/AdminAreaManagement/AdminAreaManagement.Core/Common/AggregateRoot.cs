namespace AdminAreaManagement.Core.Commun
{
    public abstract class AggregateRoot : Entity
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