using AuthManager.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

internal static class LifecycleMapping
{
    public static void Configure(ModelBuilder model, AuthDbContext context)
    {
        var revision = model.Entity<LifecycleRegistryRevisionRecord>();
        revision.ToTable("LifecycleParticipantRegistryRevisions");
        revision.HasKey(x => x.Id);
        revision.Property(x => x.ReviewReference).HasMaxLength(200);
        revision.Property(x => x.InventoryHash).HasMaxLength(64);
        revision.HasMany(x => x.Bindings).WithOne().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        var binding = model.Entity<LifecycleRegistryBindingRecord>();
        binding.ToTable("LifecycleParticipantRegistryBindings");
        binding.HasKey(x => new { x.RevisionId, x.Family, x.CapabilityKey, x.OwnershipScope });
        binding.Property(x => x.CapabilityKey).HasMaxLength(200);
        binding.Property(x => x.OwnershipScope).HasMaxLength(200);
        binding.Property(x => x.ParticipantId).HasMaxLength(200);
        var activation = model.Entity<LifecycleRegistryActivationRecord>();
        activation.ToTable("LifecycleParticipantRegistryActivation", t => t.HasCheckConstraint("CK_LifecycleActivation_Singleton", "\"Id\" = 1"));
        activation.HasKey(x => x.Id);
        activation.Property(x => x.Id).ValueGeneratedNever();
        activation.Property(x => x.Version).IsConcurrencyToken();
        activation.HasOne<LifecycleRegistryRevisionRecord>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);

        var operation = model.Entity<LifecycleOperation>();
        operation.ToTable("OrganisationLifecycleOperations");
        operation.HasKey(x => x.Id);
        operation.HasAlternateKey(x => new { x.Id, x.OrganisationId });
        operation.Property(x => x.InventoryHash).HasMaxLength(64);
        operation.Property(x => x.Revision).IsConcurrencyToken();
        operation.HasIndex(x => new { x.OrganisationId, x.Family }).IsUnique().HasFilter("\"IsActive\"");
        operation.HasIndex(x => new { x.OrganisationId, x.IdempotencyId }).IsUnique();
        operation.HasOne<LifecycleRegistryRevisionRecord>().WithMany().HasForeignKey(x => x.RegistryRevision).OnDelete(DeleteBehavior.Restrict);
        operation.HasMany(x => x.Participants).WithOne().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        operation.Navigation(x => x.Participants).UsePropertyAccessMode(PropertyAccessMode.Field);
        operation.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);
        var participant = model.Entity<LifecycleParticipant>();
        participant.ToTable("OrganisationLifecycleParticipants");
        participant.HasKey(x => new { x.OperationId, x.Family, x.CapabilityKey, x.OwnershipScope });
        participant.Property(x => x.CapabilityKey).HasMaxLength(200);
        participant.Property(x => x.OwnershipScope).HasMaxLength(200);
        participant.Property(x => x.ParticipantId).HasMaxLength(200);
        participant.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);
        foreach (var property in operation.Metadata.GetProperties().Where(p => p.Name != nameof(LifecycleOperation.Revision)
                     && p.Name != nameof(LifecycleOperation.State)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        foreach (var property in participant.Metadata.GetProperties()) property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
