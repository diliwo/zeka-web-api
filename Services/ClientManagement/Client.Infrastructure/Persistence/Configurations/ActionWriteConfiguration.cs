using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations;

public sealed class InteractionConfiguration : IEntityTypeConfiguration<Interaction>
{
    public void Configure(EntityTypeBuilder<Interaction> b)
    {
        b.ToTable("Interactions");
        b.HasKey(x => x.Id);
        b.HasOne<Client>().WithMany().HasForeignKey(x => new { x.ClientId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SocialCase>().WithMany().HasForeignKey(x => new { x.SocialCaseId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganisationId, x.ClientId, x.OccurredAtUtc });
        b.Property(x => x.Note).HasMaxLength(2000).IsRequired();
        b.ToTable(t => t.HasCheckConstraint("CK_Interactions_Revision", "\"Revision\" = 1"));
    }
}

public sealed class FollowUpActionConfiguration : IEntityTypeConfiguration<FollowUpAction>
{
    public void Configure(EntityTypeBuilder<FollowUpAction> b)
    {
        b.ToTable("FollowUpActions");
        b.HasKey(x => x.Id);
        b.HasOne<Client>().WithMany().HasForeignKey(x => new { x.ClientId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SocialCase>().WithMany().HasForeignKey(x => new { x.SocialCaseId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Interaction>().WithMany().HasForeignKey(x => new { x.InteractionId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganisationId, x.ClientId, x.State, x.DueLocalDate });
        b.Property(x => x.Purpose).HasMaxLength(500).IsRequired();
        b.Property(x => x.State).HasMaxLength(16).IsRequired();
        b.Property(x => x.Version).IsConcurrencyToken();
        b.ToTable(t => t.HasCheckConstraint("CK_FollowUpActions_State", "\"State\" IN ('Open', 'Completed')"));
    }
}

public sealed class ActionChangeConfiguration : IEntityTypeConfiguration<ActionChange>
{
    public void Configure(EntityTypeBuilder<ActionChange> b)
    {
        b.ToTable("ActionChanges");
        b.HasKey(x => x.Id);
        b.HasOne<FollowUpAction>().WithMany().HasForeignKey(x => new { x.FollowUpActionId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.OrganisationId, x.FollowUpActionId, x.NewVersion }).IsUnique();
        b.Property(x => x.Operation).HasMaxLength(32).IsRequired();
        b.Property(x => x.PriorState).HasMaxLength(16).IsRequired();
        b.Property(x => x.NewState).HasMaxLength(16).IsRequired();
    }
}

public sealed class IdempotencyReceiptConfiguration : IEntityTypeConfiguration<IdempotencyReceipt>
{
    public void Configure(EntityTypeBuilder<IdempotencyReceipt> b)
    {
        b.ToTable("IdempotencyReceipts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrganisationId, x.ActorMembershipId, x.Operation, x.ClientKey }).IsUnique();
        b.HasOne<Client>().WithMany().HasForeignKey(x => new { x.ClientId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Interaction>().WithMany().HasForeignKey(x => new { x.InteractionId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FollowUpAction>().WithMany().HasForeignKey(x => new { x.FollowUpActionId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ActionChange>().WithMany().HasForeignKey(x => new { x.ActionChangeId, x.OrganisationId })
            .HasPrincipalKey(x => new { x.Id, x.OrganisationId }).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Operation).HasMaxLength(64).IsRequired();
        b.Property(x => x.ClientKey).HasMaxLength(128).IsRequired();
        b.Property(x => x.CanonicalMetadata).HasMaxLength(1000).IsRequired();
    }
}
