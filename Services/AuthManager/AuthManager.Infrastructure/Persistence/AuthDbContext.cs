using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence.Configurations;
using AuthManager.Core.Organisations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using AuthManager.Infrastructure.Persistence.Entities;

namespace AuthManager.Infrastructure.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options)
    : IdentityDbContext<User, IdentityRole<Guid>, Guid>(options)
{
    internal Guid LifecycleOrganisationId { get; set; }
    internal bool LifecycleOnly { get; set; }
    internal System.Data.Common.DbTransaction? LifecycleTransaction { get; set; }

    protected override void OnConfiguring(DbContextOptionsBuilder builder) =>
        builder.AddInterceptors(new Lifecycle.LifecycleCommandGuard(this));

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    { GuardLifecycleWrites(); return base.SaveChanges(acceptAllChangesOnSuccess); }
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    { GuardLifecycleWrites(); return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }

    private void GuardLifecycleWrites()
    {
        ChangeTracker.DetectChanges();
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            if (entry.Entity is Lifecycle.LifecycleRegistryRevisionRecord or Lifecycle.LifecycleRegistryBindingRecord
                && entry.State != EntityState.Added)
                throw new InvalidOperationException("Registry revisions and bindings are immutable.");
            if (entry.Entity is MembershipPermissionGrant && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Membership permission grant history is append-preserving.");
            if (entry.Entity is MembershipPermissionGrant && entry.State == EntityState.Modified
                && (entry.Property(nameof(MembershipPermissionGrant.OrganisationId)).IsModified
                    || entry.Property(nameof(MembershipPermissionGrant.OrganisationMembershipId)).IsModified
                    || entry.Property(nameof(MembershipPermissionGrant.PermissionKey)).IsModified
                    || entry.Property(nameof(MembershipPermissionGrant.GrantedByMembershipId)).IsModified
                    || entry.Property(nameof(MembershipPermissionGrant.GrantedBySubjectId)).IsModified
                    || entry.Property(nameof(MembershipPermissionGrant.GrantedAtUtc)).IsModified))
                throw new InvalidOperationException("Membership permission grant authority evidence is immutable.");
            if (entry.Entity is AuthManager.Core.Lifecycle.LifecycleOperation or AuthManager.Core.Lifecycle.LifecycleParticipant)
            {
                var organisation = (Guid)entry.Property("OrganisationId").CurrentValue!;
                if (!LifecycleOnly || LifecycleTransaction is null || organisation == Guid.Empty
                    || organisation != LifecycleOrganisationId || entry.State == EntityState.Deleted
                    || entry.State == EntityState.Modified && (Guid)entry.Property("OrganisationId").OriginalValue! != organisation)
                    throw new InvalidOperationException("Lifecycle write is outside its immutable organisation attempt.");
            }
            if (entry.Entity is AuthManager.Core.Lifecycle.LifecycleInboxReceipt
                or AuthManager.Core.Lifecycle.LifecycleExportFenceReceipt
                or AuthManager.Core.Lifecycle.LifecycleExportFragment
                or AuthManager.Core.Lifecycle.LifecycleExportPackage
                or AuthManager.Core.Lifecycle.LifecycleCoordinatorLease)
            {
                var organisation = (Guid)entry.Property("OrganisationId").CurrentValue!;
                if (!LifecycleOnly || LifecycleTransaction is null || organisation == Guid.Empty
                    || organisation != LifecycleOrganisationId || entry.State == EntityState.Deleted
                    || entry.State == EntityState.Modified && (Guid)entry.Property("OrganisationId").OriginalValue! != organisation)
                    throw new InvalidOperationException("Lifecycle evidence write is outside its immutable organisation attempt.");
            }
        }
    }
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Organisation> Organisations => Set<Organisation>();
    public DbSet<OrganisationMembership> OrganisationMemberships => Set<OrganisationMembership>();
    public DbSet<PermissionSet> PermissionSets => Set<PermissionSet>();
    public DbSet<MembershipPermissionGrant> MembershipPermissionGrants => Set<MembershipPermissionGrant>();
    public DbSet<AuthManager.Core.Lifecycle.LifecycleInboxReceipt> LifecycleInboxReceipts => Set<AuthManager.Core.Lifecycle.LifecycleInboxReceipt>();
    public DbSet<AuthManager.Core.Lifecycle.LifecycleExportFenceReceipt> LifecycleExportFenceReceipts => Set<AuthManager.Core.Lifecycle.LifecycleExportFenceReceipt>();
    public DbSet<AuthManager.Core.Lifecycle.LifecycleExportFragment> LifecycleExportFragments => Set<AuthManager.Core.Lifecycle.LifecycleExportFragment>();
    public DbSet<AuthManager.Core.Lifecycle.LifecycleExportPackage> LifecycleExportPackages => Set<AuthManager.Core.Lifecycle.LifecycleExportPackage>();
    public DbSet<AuthManager.Core.Lifecycle.LifecycleCoordinatorLease> LifecycleCoordinatorLeases => Set<AuthManager.Core.Lifecycle.LifecycleCoordinatorLease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        Lifecycle.LifecycleMapping.Configure(modelBuilder, this);
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEntryConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OrganisationConfiguration());
        modelBuilder.ApplyConfiguration(new OrganisationMembershipConfiguration());
        modelBuilder.ApplyConfiguration(new PermissionSetConfiguration());
        modelBuilder.ApplyConfiguration(new MembershipPermissionGrantConfiguration());
    }
}
