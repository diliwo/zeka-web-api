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

        public ApplicationDbContext(
            DbContextOptions options,
            ICurrentUserService currentUserService,
            //IDomainEventService domainEventService,
            IDateTime dateTime
            ) : base(options)
        {
            _currentUserService = currentUserService;
            //_domainEventService = domainEventService;
            _dateTime = dateTime;
        }

        public DbSet<Beneficiary> Beneficiaries { get; set; }
        public DbSet<Support> Supports { get; set; }
        public DbSet<Referent> Referents { get; set; }
        public DbSet<Service> Services { get; set; }
        //public DbSet<Partner> Partners { get; set; }
        //public DbSet<Job> Jobs { get; set; }
        //public DbSet<JobOffer> JobOffers { get; set; }
        //public DbSet<Candidacy> Candidacies { get; set; }
        //public DbSet<DocumentPartner> DocumentPartners { get; set; }
        public DbSet<SchoolRegistration> SchoolRegistrations { get; set; }
        //public DbSet<School> Schools { get; set; }
        public DbSet<Formation> Formations { get; set; }
        //public DbSet<ProfessionnalExperience> ProfessionnalExperiences { get; set; }
        public DbSet<Bilan> Bilans { get; set; }
        public DbSet<QuarterlyMonitoring> QuarterlyMonitorings { get; set; }
        public DbSet<MonitoringAction> MonitoringActions { get; set; }
        public DbSet<BilanProfession> ProfessionBilans { get; set; }
        public DbSet<Profession> Professions { get; set; }
        public DbSet<Language> Languages { get; set; }
        public DbSet<TrainingType> TrainingTypes { get; set; }
        public DbSet<TrainingField> TrainingFields { get; set; }
        public DbSet<NatureOfContract> NatureOfContract { get; set; }
        public DbSet<AnnualClosure> AnnualClosures { get; set; }
        public DbSet<ContratPiis> ContratPiis { get; set; }
        //public DbSet<EmploymentStatusHistory> EmploymentStatusHistories { get; set; }
        //public DbSet<EmploymentTerminationType> EmploymentTerminationTypes { get; set; }
        //public DbSet<EmploymentTerminationReason> EmploymentTerminationReasons { get; set; }
        //public DbSet<TerminationReasonForEmployment> TerminationReasonForEmployments { get; set; }
        public DbSet<AnnualClosureHandlingHistory> AnnualClosuresHandlingHistories { get; set; }

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
