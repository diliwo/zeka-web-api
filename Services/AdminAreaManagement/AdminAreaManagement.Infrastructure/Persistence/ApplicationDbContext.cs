using System.Reflection;
using AdminAreaManagement.Application.Staffs.Queries;
using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class ApplicationDbContext : DbContext
    {
        private readonly ICurrentUserService _currentUserService;
        private readonly IDateTime _dateTime;
        //private readonly IDomainEventService _domainEventService;

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

        public DbSet<StaffMember> StaffMembers { get; set; }
        public DbSet<Team> Teams { get; set; }
        public DbSet<Partner> Partners { get; set; }
        public DbSet<DocumentPartner> DocumentPartners { get; set; }
        public DbSet<School> Schools { get; set; }
        public DbSet<Training> Trainings { get; set; }
        public DbSet<Profession> Professions { get; set; }
        public DbSet<TrainingType> TrainingTypes { get; set; }
        public DbSet<TrainingField> TrainingFields { get; set; }
        public DbSet<City> Cities { get; set; }
        public DbSet<Nationality> Nationalities { get; set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry in ChangeTracker.Entries<Entity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.Created = DateTime.Now;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.LastModified = DateTime.Now;
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
                        entry.Entity.CreatedBy = "ZeKa"; //TODO: This will be replaced by Identity Server
                        entry.Entity.Created = DateTime.Now;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = "ZeKa"; //TODO: This will be replaced by Identity Server
                        entry.Entity.LastModified = DateTime.Now;
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

            foreach (var entityType in builder.Model.GetEntityTypes()
                         .Where(type => typeof(TenantOwnedEntity).IsAssignableFrom(type.ClrType) && !type.IsOwned()))
            {
                builder.Entity(entityType.ClrType)
                    .HasAlternateKey(nameof(Entity.Id), nameof(TenantOwnedEntity.OrganisationId));
            }

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
