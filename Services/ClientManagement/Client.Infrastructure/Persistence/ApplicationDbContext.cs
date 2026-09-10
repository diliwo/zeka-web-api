using ClientManagement.Core.Common;
using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Zeka.Extensions.MultiTenancy.Abstractions;
using Zeka.Extensions.MultiTenancy.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence;

public class ApplicationDbContext : TenantDbContext
{
    private readonly ClientManagement.Application.Common.Authorization.TenantOperation? operation;
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContextAccessor tenant,
        ClientManagement.Application.Common.Authorization.TenantOperation operation) : this(options, tenant)
        => this.operation = operation;
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ITenantContextAccessor tenant)
        : base(WithAudit(options), tenant) { }

    // Model inspection is permitted without a scope; runtime queries and writes still fail closed.
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : this(options, new TenantContextScope()) { }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, PlatformAccessContext platform)
        : base(WithAudit(options), platform) { }

    private static DbContextOptions WithAudit(DbContextOptions options) =>
        new DbContextOptionsBuilder(options).AddInterceptors(new EntityAuditInterceptor(), new AssignedWriteInterceptor()).Options;

    public IQueryable<T> Visible<T>() where T : class
    {
        var query = Set<T>().AsQueryable();
        if (operation?.AssignedOnly != true) return query;
        var clients = AssignedClients().Select(c => c.Id);
        if (typeof(T) == typeof(Client)) return (IQueryable<T>)AssignedClients();
        if (typeof(T) == typeof(SocialCase)) return (IQueryable<T>)SocialCases.Where(x => clients.Contains(x.ClientId));
        if (typeof(T) == typeof(SchoolRegistration)) return (IQueryable<T>)SchoolRegistrations.Where(x => clients.Contains(x.ClientId));
        if (typeof(T) == typeof(MonitoringReport)) return (IQueryable<T>)MonitoringReports.Where(x => clients.Contains(x.ClientId));
        if (typeof(T) == typeof(Assessment)) return (IQueryable<T>)Assessments.Where(x => x.ClientId != null && clients.Contains(x.ClientId.Value));
        if (typeof(T) == typeof(ProfessionalAssessment)) return (IQueryable<T>)ProfessionalAssessments.Where(x =>
            x.Assessment.ClientId != null && clients.Contains(x.Assessment.ClientId.Value));
        if (typeof(T) == typeof(ProfessionnalExperience)) return (IQueryable<T>)Set<ProfessionnalExperience>().Where(x => clients.Contains(x.ClientId));
        if (typeof(T) == typeof(SocialWorker)) return (IQueryable<T>)SocialWorkers.Where(x => x.OrganisationMembershipId == operation.MembershipId);
        if (typeof(IGlobalEntity).IsAssignableFrom(typeof(T))) return query;
        throw new ClientManagement.Application.Common.Authorization.TenantAccessException(
            ClientManagement.Application.Common.Authorization.AccessFailure.Denied);
    }

    private IQueryable<Client> AssignedClients()
    {
        var membership = operation!.MembershipId;
        return Clients.Where(c => !c.Softdelete
            && c.SocialCases.Count(s => !s.Softdelete && s.EndDate == null) == 1
            && c.SocialCases.Any(s => !s.Softdelete && s.EndDate == null && !s.SocialWorker.Softdelete
                && s.SocialWorker.OrganisationId == c.OrganisationId
                && s.SocialWorker.OrganisationMembershipId == membership));
    }

    internal void ValidateAssignedWrites()
    {
        if (operation?.AssignedOnly != true) return;
        foreach (var entry in ChangeTracker.Entries<TenantOwnedEntity>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            int? clientId = entry.Entity switch
            {
                Client client => client.Id,
                SocialCase support => support.ClientId,
                SchoolRegistration school => school.ClientId,
                MonitoringReport report => report.ClientId,
                ProfessionnalExperience experience => experience.ClientId,
                Assessment assessment => assessment.ClientId,
                ProfessionalAssessment professional => Assessments.Where(a => a.Id == professional.AssessmentId)
                    .Select(a => a.ClientId).SingleOrDefault(),
                _ => null
            };
            if (clientId is null || !AssignedClients().Any(c => c.Id == clientId)) Deny();
            if (entry.State != EntityState.Added)
            {
                var id = entry.Entity.Id;
                var owned = entry.Entity switch
                {
                    Client => Visible<Client>().Any(x => x.Id == id),
                    SocialCase => Visible<SocialCase>().Any(x => x.Id == id),
                    SchoolRegistration => Visible<SchoolRegistration>().Any(x => x.Id == id),
                    MonitoringReport => Visible<MonitoringReport>().Any(x => x.Id == id),
                    ProfessionnalExperience => Visible<ProfessionnalExperience>().Any(x => x.Id == id),
                    Assessment => Visible<Assessment>().Any(x => x.Id == id),
                    ProfessionalAssessment => Visible<ProfessionalAssessment>().Any(x => x.Id == id),
                    _ => false
                };
                if (!owned) Deny();
            }
            if (entry.Entity is SocialCase supportChange && !SocialWorkers.Any(w => w.Id == supportChange.SocialWorkerId
                && w.OrganisationMembershipId == operation.MembershipId && !w.Softdelete)) Deny();
        }
    }

    private static void Deny() => throw new ClientManagement.Application.Common.Authorization.TenantAccessException(
        ClientManagement.Application.Common.Authorization.AccessFailure.Denied);

    public int SaveChanges(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return SaveChanges();
    }

    public DbSet<Client> Clients => Set<Client>();
    public DbSet<SocialCase> SocialCases => Set<SocialCase>();
    public DbSet<SchoolRegistration> SchoolRegistrations => Set<SchoolRegistration>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<MonitoringReport> MonitoringReports => Set<MonitoringReport>();
    public DbSet<MonitoringAction> MonitoringActions => Set<MonitoringAction>();
    public DbSet<ProfessionalAssessment> ProfessionalAssessments => Set<ProfessionalAssessment>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<SocialWorker> SocialWorkers => Set<SocialWorker>();

    protected override void ConfigureTenantModel(ModelBuilder builder) => ConfigurePersistenceModel(builder);

    internal static void ConfigurePersistenceModel(ModelBuilder builder)
    {
        builder.Ignore<ClientManagement.Core.ValueObjects.Address>();
        builder.Ignore<ClientManagement.Core.ValueObjects.Email>();
        builder.Ignore<ClientManagement.Core.ValueObjects.Phone>();
        builder.Ignore<ClientManagement.Core.ValueObjects.Language>();
        builder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
        // Keep the established column types; UTC schema standardization belongs to issue #25.
        foreach (var property in builder.Model.GetEntityTypes().SelectMany(type => type.GetProperties())
            .Where(property => property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)))
        {
            if (property.GetColumnType() != "date")
            {
                property.SetColumnType("timestamp without time zone");
                var audit = property.Name is nameof(Entity.Created) or nameof(Entity.LastModified);
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
                    value => DateTime.SpecifyKind(value, DateTimeKind.Unspecified),
                    value => DateTime.SpecifyKind(value, audit ? DateTimeKind.Utc : DateTimeKind.Unspecified)));
            }
        }
        foreach (var type in builder.Model.GetEntityTypes()
            .Where(type => typeof(TenantOwnedEntity).IsAssignableFrom(type.ClrType)))
            builder.Entity(type.ClrType).HasAlternateKey(nameof(Entity.Id), nameof(TenantOwnedEntity.OrganisationId));
    }
}

internal sealed class AssignedWriteInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    { ((ApplicationDbContext)data.Context!).ValidateAssignedWrites(); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { ((ApplicationDbContext)data.Context!).ValidateAssignedWrites(); return ValueTask.FromResult(result); }
}

// Only deployment tooling constructs this context. It is never registered in application DI.
public sealed class DeploymentDbContext(DbContextOptions<DeploymentDbContext> options) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<SocialCase> SocialCases => Set<SocialCase>();
    public DbSet<SchoolRegistration> SchoolRegistrations => Set<SchoolRegistration>();
    public DbSet<Assessment> Assessments => Set<Assessment>();
    public DbSet<MonitoringReport> MonitoringReports => Set<MonitoringReport>();
    public DbSet<MonitoringAction> MonitoringActions => Set<MonitoringAction>();
    public DbSet<ProfessionalAssessment> ProfessionalAssessments => Set<ProfessionalAssessment>();
    public DbSet<Language> Languages => Set<Language>();
    public DbSet<SocialWorker> SocialWorkers => Set<SocialWorker>();
    protected override void OnModelCreating(ModelBuilder builder) => ApplicationDbContext.ConfigurePersistenceModel(builder);
}

internal sealed class EntityAuditInterceptor : SaveChangesInterceptor
{
    private static void Stamp(DbContext? context)
    {
        if (context is null) return;
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedBy = "ZeKa";
                entry.Entity.Created = DateTime.UtcNow;
            }
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastModifiedBy = "ZeKa";
                entry.Entity.LastModified = DateTime.UtcNow;
            }
        }
    }
    public override InterceptionResult<int> SavingChanges(DbContextEventData data, InterceptionResult<int> result)
    { Stamp(data.Context); return result; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Stamp(data.Context); return ValueTask.FromResult(result); }
}
