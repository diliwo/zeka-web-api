using System.Reflection;
using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class ApplicationDbContext :  DbContext
    {
        private readonly ICurrentUserService _currentUserService;
        private readonly IDateTime _dateTime;
        //private readonly IDomainEventService _domainEventService;

        public ApplicationDbContext(DbContextOptions options) : base(options) { }

        public DbSet<Beneficiary> Beneficiaries { get; set; }
        public DbSet<Support> Supports { get; set; }
        public DbSet<SchoolRegistration> SchoolRegistrations { get; set; }
        public DbSet<Bilan> Bilans { get; set; }
        public DbSet<QuarterlyMonitoring> QuarterlyMonitorings { get; set; }
        public DbSet<MonitoringAction> MonitoringActions { get; set; }
        public DbSet<BilanProfession> ProfessionBilans { get; set; }
        public DbSet<Language> Languages { get; set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry in ChangeTracker.Entries<Entity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedBy = _currentUserService.Username;
                        entry.Entity.Created = _dateTime.Now;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = _currentUserService.Username;
                        entry.Entity.LastModified = _dateTime.Now;
                        break;
                }
            }

            var result = await base.SaveChangesAsync(cancellationToken);

            await DispatchEvents();

            return result;
        }


        public int SaveChanges(CancellationToken cancellationToken = new CancellationToken())
        {
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry in ChangeTracker.Entries<Entity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedBy = _currentUserService.Username;
                        entry.Entity.Created = _dateTime.Now;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = _currentUserService.Username;
                        entry.Entity.LastModified = _dateTime.Now;
                        break;
                }
            }

            var result = base.SaveChanges();

            DispatchEvents();

            return result;
        }
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            base.OnModelCreating(builder);
        }

        private async Task DispatchEvents()
        {
            while (true)
            {
                var domainEventEntity = ChangeTracker.Entries<IHasDomainEvent>()
                    .Select(x => x.Entity.DomainEvents)
                    .SelectMany(x => x)
                    .Where(domainEvent => !domainEvent.IsPublished)
                    .FirstOrDefault();
                if (domainEventEntity == null) break;

                domainEventEntity.IsPublished = true;
                //await _domainEventService.Publish(domainEventEntity);
            }
        }
    }
}
