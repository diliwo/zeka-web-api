using AdminAreaManagement.Core.Common;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Infrastructure.Persistence.Exports;
using AdminAreaManagement.Infrastructure.Persistence.Closures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Extensions.MultiTenancy.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence;

public class ApplicationDbContext : TenantDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContextAccessor tenant)
        : base(WithAudit(options), tenant) { }

    // Model inspection is permitted without a scope; runtime queries and writes still fail closed.
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, new TenantContextScope()) { }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, PlatformAccessContext platform)
        : base(WithAudit(options), platform) { }

    private static DbContextOptions WithAudit(DbContextOptions options) =>
        new DbContextOptionsBuilder(options).AddInterceptors(new EntityAuditInterceptor()).Options;

    public int SaveChanges(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SaveChanges();
    }


    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<DocumentPartner> DocumentPartners => Set<DocumentPartner>();
    public DbSet<School> Schools => Set<School>();
    public DbSet<Training> Trainings => Set<Training>();
    public DbSet<Profession> Professions => Set<Profession>();
    public DbSet<TrainingType> TrainingTypes => Set<TrainingType>();
    public DbSet<TrainingField> TrainingFields => Set<TrainingField>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<Nationality> Nationalities => Set<Nationality>();
    public DbSet<AdminAreaExportFence> AdminAreaExportFences => Set<AdminAreaExportFence>();
    public DbSet<AdminAreaExportInbox> AdminAreaExportInbox => Set<AdminAreaExportInbox>();
    public DbSet<AdminAreaExportOutbox> AdminAreaExportOutbox => Set<AdminAreaExportOutbox>();
    public DbSet<AdminAreaExportFragment> AdminAreaExportFragments => Set<AdminAreaExportFragment>();
    public DbSet<AdminAreaClosureFence> AdminAreaClosureFences => Set<AdminAreaClosureFence>();
    public DbSet<AdminAreaClosureInbox> AdminAreaClosureInbox => Set<AdminAreaClosureInbox>();
    public DbSet<AdminAreaClosureOutbox> AdminAreaClosureOutbox => Set<AdminAreaClosureOutbox>();

    protected override void ConfigureTenantModel(ModelBuilder builder) => ConfigurePersistenceModel(builder);

    public static string CityKey(string value) => throw new InvalidOperationException("Database function only.");

    internal static void ConfigurePersistenceModel(ModelBuilder builder)
    {
        builder.Ignore<AdminAreaManagement.Core.ValueObjects.Address>();
        builder.HasDbFunction(typeof(ApplicationDbContext).GetMethod(nameof(CityKey))!)
            .HasName("zeka_city_key").HasSchema("public");
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        // Keep the established column types; UTC schema standardization belongs to issue #25.
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(type => type.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)))
        {
            if (property.GetColumnType() != "date") property.SetColumnType("timestamp without time zone");
        }
        foreach (var type in builder.Model.GetEntityTypes()
            .Where(type => typeof(TenantOwnedEntity).IsAssignableFrom(type.ClrType)))
            builder.Entity(type.ClrType).HasAlternateKey(nameof(Entity.Id), nameof(TenantOwnedEntity.OrganisationId));
    }
}

// Only deployment tooling constructs this context. It is never registered in application DI.
public sealed class DeploymentDbContext(DbContextOptions<DeploymentDbContext> options) : DbContext(options)
{
    public DbSet<StaffMember> StaffMembers => Set<StaffMember>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<DocumentPartner> DocumentPartners => Set<DocumentPartner>();
    public DbSet<School> Schools => Set<School>();
    public DbSet<Training> Trainings => Set<Training>();
    public DbSet<Profession> Professions => Set<Profession>();
    public DbSet<TrainingType> TrainingTypes => Set<TrainingType>();
    public DbSet<TrainingField> TrainingFields => Set<TrainingField>();
    public DbSet<City> Cities => Set<City>();
    public DbSet<Nationality> Nationalities => Set<Nationality>();
    public DbSet<AdminAreaExportFence> AdminAreaExportFences => Set<AdminAreaExportFence>();
    public DbSet<AdminAreaExportInbox> AdminAreaExportInbox => Set<AdminAreaExportInbox>();
    public DbSet<AdminAreaExportOutbox> AdminAreaExportOutbox => Set<AdminAreaExportOutbox>();
    public DbSet<AdminAreaExportFragment> AdminAreaExportFragments => Set<AdminAreaExportFragment>();
    public DbSet<AdminAreaClosureFence> AdminAreaClosureFences => Set<AdminAreaClosureFence>();
    public DbSet<AdminAreaClosureInbox> AdminAreaClosureInbox => Set<AdminAreaClosureInbox>();
    public DbSet<AdminAreaClosureOutbox> AdminAreaClosureOutbox => Set<AdminAreaClosureOutbox>();
    protected override void OnModelCreating(ModelBuilder builder) => ApplicationDbContext.ConfigurePersistenceModel(builder);
}

internal sealed class EntityAuditInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> DocumentRecoveryProperties =
    [
        nameof(DocumentPartner.FileWriteState), nameof(DocumentPartner.FileWriteAttempts),
        nameof(DocumentPartner.FileWriteFailureCode), nameof(DocumentPartner.PendingFileContent),
        nameof(DocumentPartner.FileDeleteState), nameof(DocumentPartner.FileDeleteAttempts),
        nameof(DocumentPartner.FileDeleteFailureCode)
    ];

    private static void Stamp(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedBy = "ZeKa";
                entry.Entity.Created = DateTime.Now;
            }
            if (entry.State == EntityState.Modified)
            {
                if (entry.Entity is DocumentPartner
                    && entry.Properties.Where(property => property.IsModified)
                        .All(property => DocumentRecoveryProperties.Contains(property.Metadata.Name)))
                    continue;
                entry.Entity.LastModifiedBy = "ZeKa";
                entry.Entity.LastModified = DateTime.Now;
            }
        }
    }
    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    { Stamp(data.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Stamp(data.Context); return ValueTask.FromResult(result); }
}
