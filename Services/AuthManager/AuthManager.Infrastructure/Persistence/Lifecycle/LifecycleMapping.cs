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
        // Canonical bytes are hash-bound; jsonb would normalize their representation on round-trip.
        operation.Property(x => x.ExportInventoryJson).HasColumnType("text");
        operation.Property(x => x.ExportInventoryHash).HasMaxLength(64);
        operation.Property(x => x.FenceEvidenceHash).HasMaxLength(64);
        operation.Property(x => x.ClosureFenceEvidenceHash).HasMaxLength(64);
        operation.Property(x => x.PackageSha256).HasMaxLength(64);
        operation.Property(x => x.PackageReference).HasMaxLength(500);
        operation.Property(x => x.FailureCode).HasMaxLength(200);
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
        var mutableOperationProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LifecycleOperation.State), nameof(LifecycleOperation.Revision), nameof(LifecycleOperation.SnapshotAt),
            nameof(LifecycleOperation.FenceEvidenceHash), nameof(LifecycleOperation.PackageSha256),
            nameof(LifecycleOperation.PackageReference), nameof(LifecycleOperation.FailureCode),
            nameof(LifecycleOperation.CompletedAt), nameof(LifecycleOperation.IsActive),
            nameof(LifecycleOperation.ClosingAt), nameof(LifecycleOperation.ArchivedAt),
            nameof(LifecycleOperation.ClosureFenceEvidenceHash)
        };
        foreach (var property in operation.Metadata.GetProperties().Where(p => !mutableOperationProperties.Contains(p.Name)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        var mutableParticipantProperties = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LifecycleParticipant.State), nameof(LifecycleParticipant.FailureCode),
            nameof(LifecycleParticipant.FailureRetryable), nameof(LifecycleParticipant.FailedAt),
            nameof(LifecycleParticipant.FailureBoundaryDisposition)
        };
        foreach (var property in participant.Metadata.GetProperties().Where(p => !mutableParticipantProperties.Contains(p.Name)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        var inbox = model.Entity<LifecycleInboxReceipt>();
        inbox.ToTable("LifecycleInboxReceipts");
        inbox.HasKey(x => x.MessageId);
        inbox.Property(x => x.MessageType).HasMaxLength(300);
        inbox.Property(x => x.PayloadSha256).HasMaxLength(64);
        inbox.HasIndex(x => new { x.OperationId, x.MessageType });
        inbox.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        inbox.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var receipt = model.Entity<LifecycleExportFenceReceipt>();
        receipt.ToTable("LifecycleExportFenceReceipts");
        receipt.HasKey(x => new { x.OperationId, x.ParticipantId });
        receipt.Property(x => x.ParticipantId).HasMaxLength(200);
        receipt.Property(x => x.FenceToken).HasMaxLength(200);
        receipt.Property(x => x.ReceiptHash).HasMaxLength(64);
        receipt.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        receipt.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var fragment = model.Entity<LifecycleExportFragment>();
        fragment.ToTable("LifecycleExportFragments");
        fragment.HasKey(x => new { x.OperationId, x.ParticipantId });
        fragment.Property(x => x.ParticipantId).HasMaxLength(200);
        fragment.Property(x => x.FenceToken).HasMaxLength(200);
        fragment.Property(x => x.FragmentHash).HasMaxLength(64);
        fragment.Property(x => x.CategoriesJson).HasColumnType("jsonb");
        fragment.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        fragment.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var package = model.Entity<LifecycleExportPackage>();
        package.ToTable("LifecycleExportPackages");
        package.HasKey(x => x.OperationId);
        package.Property(x => x.ManifestSha256).HasMaxLength(64);
        package.Property(x => x.PackageSha256).HasMaxLength(64);
        package.Property(x => x.PackageReference).HasMaxLength(500);
        package.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        package.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var lease = model.Entity<LifecycleCoordinatorLease>();
        lease.ToTable("LifecycleCoordinatorLeases");
        lease.HasKey(x => x.OperationId);
        lease.Property(x => x.Version).IsConcurrencyToken();
        lease.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        lease.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var authExecution = model.Entity<AuthExportParticipantExecution>();
        authExecution.ToTable("AuthExportParticipantExecutions");
        authExecution.HasKey(x => x.OperationId);
        authExecution.Property(x => x.FenceToken).HasMaxLength(200);
        authExecution.Property(x => x.FenceReceiptHash).HasMaxLength(64);
        authExecution.Property(x => x.FenceEvidenceHash).HasMaxLength(64);
        authExecution.Property(x => x.FragmentHash).HasMaxLength(64);
        authExecution.Property(x => x.CategoriesJson).HasColumnType("jsonb");
        authExecution.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        authExecution.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var authInbox = model.Entity<AuthExportParticipantInboxReceipt>();
        authInbox.ToTable("AuthExportParticipantInbox");
        authInbox.HasKey(x => x.MessageId);
        authInbox.Property(x => x.MessageType).HasMaxLength(300);
        authInbox.Property(x => x.PayloadSha256).HasMaxLength(64);
        authInbox.HasIndex(x => new { x.OperationId, x.MessageType });
        authInbox.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        authInbox.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var authOutbox = model.Entity<AuthExportParticipantOutboxMessage>();
        authOutbox.ToTable("AuthExportParticipantOutbox");
        authOutbox.HasKey(x => x.MessageId);
        authOutbox.Property(x => x.MessageType).HasMaxLength(300);
        authOutbox.Property(x => x.PayloadJson).HasColumnType("jsonb");
        authOutbox.Property(x => x.PayloadSha256).HasMaxLength(64);
        authOutbox.HasIndex(x => new { x.OperationId, x.MessageType }).IsUnique();
        authOutbox.HasOne<LifecycleOperation>().WithMany().HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        authOutbox.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var closureReceipt = model.Entity<LifecycleClosureFenceReceipt>();
        closureReceipt.ToTable("LifecycleClosureFenceReceipts");
        closureReceipt.HasKey(x => new { x.OperationId, x.ParticipantId });
        closureReceipt.Property(x => x.ParticipantId).HasMaxLength(200);
        closureReceipt.Property(x => x.FenceToken).HasMaxLength(200);
        closureReceipt.Property(x => x.ReceiptHash).HasMaxLength(64);
        closureReceipt.HasOne<LifecycleOperation>().WithMany()
            .HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        closureReceipt.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var closureExecution = model.Entity<AuthClosureParticipantExecution>();
        closureExecution.ToTable("AuthClosureParticipantExecutions");
        closureExecution.HasKey(x => x.OperationId);
        closureExecution.Property(x => x.FenceToken).HasMaxLength(200);
        closureExecution.Property(x => x.ReceiptHash).HasMaxLength(64);
        closureExecution.HasOne<LifecycleOperation>().WithMany()
            .HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        closureExecution.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var closureInbox = model.Entity<AuthClosureParticipantInboxReceipt>();
        closureInbox.ToTable("AuthClosureParticipantInbox");
        closureInbox.HasKey(x => x.MessageId);
        closureInbox.Property(x => x.MessageType).HasMaxLength(300);
        closureInbox.Property(x => x.PayloadSha256).HasMaxLength(64);
        closureInbox.HasIndex(x => new { x.OperationId, x.MessageType });
        closureInbox.HasOne<LifecycleOperation>().WithMany()
            .HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        closureInbox.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        var closureOutbox = model.Entity<AuthClosureParticipantOutboxMessage>();
        closureOutbox.ToTable("AuthClosureParticipantOutbox");
        closureOutbox.HasKey(x => x.MessageId);
        closureOutbox.Property(x => x.MessageType).HasMaxLength(300);
        closureOutbox.Property(x => x.PayloadJson).HasColumnType("jsonb");
        closureOutbox.Property(x => x.PayloadSha256).HasMaxLength(64);
        closureOutbox.HasIndex(x => new { x.OperationId, x.MessageType }).IsUnique();
        closureOutbox.HasOne<LifecycleOperation>().WithMany()
            .HasForeignKey(x => new { x.OperationId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        closureOutbox.HasQueryFilter(x => x.OrganisationId == context.LifecycleOrganisationId);

        foreach (var entity in new[] { inbox.Metadata, receipt.Metadata, fragment.Metadata, package.Metadata,
                     authInbox.Metadata, authOutbox.Metadata, closureReceipt.Metadata,
                     closureInbox.Metadata, closureOutbox.Metadata })
            foreach (var property in entity.GetProperties()) property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        foreach (var property in authExecution.Metadata.GetProperties().Where(p =>
                     p.Name is not nameof(AuthExportParticipantExecution.SnapshotAt)
                         and not nameof(AuthExportParticipantExecution.FenceEvidenceHash)
                         and not nameof(AuthExportParticipantExecution.FragmentHash)
                         and not nameof(AuthExportParticipantExecution.CategoriesJson)
                         and not nameof(AuthExportParticipantExecution.ReleasedAt)
                         and not nameof(AuthExportParticipantExecution.State)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        foreach (var property in closureExecution.Metadata.GetProperties().Where(p =>
                     p.Name is not nameof(AuthClosureParticipantExecution.ReleasedAt)
                         and not nameof(AuthClosureParticipantExecution.State)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        foreach (var property in lease.Metadata.GetProperties().Where(p =>
                     p.Name is not nameof(LifecycleCoordinatorLease.LeaseId)
                         and not nameof(LifecycleCoordinatorLease.ExpiresAt)
                         and not nameof(LifecycleCoordinatorLease.Version)))
            property.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
    }
}
