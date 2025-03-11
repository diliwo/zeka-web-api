using System.Reflection;
using System.Reflection.Emit;
using ClientManagement.Core.Common;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence
{
    public class ApplicationDbContext :  DbContext
    {
        private readonly ICurrentUserService _currentUserService;
        private readonly IDateTime _dateTime;
        private readonly ITenantService _tenantService;
        //private readonly IDomainEventService _domainEventService;

        public ApplicationDbContext(DbContextOptions options, ITenantService service) : base(options) => _tenantService = service;
        public string TenantName { get => _tenantService.GetTenant()?.TenantName ?? String.Empty; }

        public DbSet<Client> Clients { get; set; }
        public DbSet<Support> Supports { get; set; }
        public DbSet<SchoolRegistration> SchoolRegistrations { get; set; }
        public DbSet<Assessment> Assessments { get; set; }
        public DbSet<MonitoringReport> MonitoringReports { get; set; }
        public DbSet<MonitoringAction> MonitoringActions { get; set; }
        public DbSet<ProfessionalAssessment> ProfessionalAssessments { get; set; }
        public DbSet<Language> Languages { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var tenantConnectionString = _tenantService.GetConnectionString();
            if (!string.IsNullOrEmpty(tenantConnectionString))
            {
                optionsBuilder.UseNpgsql(_tenantService.GetConnectionString());
            }
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = new CancellationToken())
        {
            foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<Entity> entry in ChangeTracker.Entries<Entity>())
            {
                switch (entry.State)
                {
                    case EntityState.Added:
                        entry.Entity.CreatedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.Created = DateTime.Now; ;
                        entry.Entity.TenantName = TenantName;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.LastModified = DateTime.Now; ;
                        entry.Entity.TenantName = TenantName;
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
                        entry.Entity.CreatedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.Created = DateTime.Now; ;
                        entry.Entity.TenantName = TenantName;
                        break;

                    case EntityState.Modified:
                        entry.Entity.LastModifiedBy = "ZeKa";  //TODO: This will be replaced by Identity Server
                        entry.Entity.LastModified = DateTime.Now; ;
                        entry.Entity.TenantName = TenantName;
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
            builder.Entity<Client>().HasQueryFilter(a => a.TenantName == TenantName);
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
