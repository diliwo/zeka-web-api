using ClientManagement.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations;

public sealed class OrganisationExportFenceConfiguration : IEntityTypeConfiguration<OrganisationExportFence>
{
    public void Configure(EntityTypeBuilder<OrganisationExportFence> builder)
    {
        builder.ToTable("OrganisationExportFences");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OperationId, x.OrganisationId }).IsUnique();
        builder.HasIndex(x => x.OrganisationId).IsUnique()
            .HasFilter("\"State\" = 1");
        builder.Property(x => x.ParticipantId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FenceToken).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FenceRevision).IsConcurrencyToken();
    }
}

public sealed class OrganisationExportFragmentConfiguration : IEntityTypeConfiguration<OrganisationExportFragment>
{
    public void Configure(EntityTypeBuilder<OrganisationExportFragment> builder)
    {
        builder.ToTable("OrganisationExportFragments");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OperationId, x.OrganisationId, x.Category }).IsUnique();
        builder.Property(x => x.ParticipantId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Category).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FenceToken).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FenceEvidenceHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.Disposition).HasMaxLength(32).IsRequired();
        builder.Property(x => x.SchemaVersion).HasMaxLength(200);
        builder.Property(x => x.ContentSha256).HasMaxLength(64);
        builder.Property(x => x.ArtifactReference).HasMaxLength(500);
        builder.Property(x => x.ReasonCode).HasMaxLength(200);
    }
}

public sealed class OrganisationExportCommandReceiptConfiguration : IEntityTypeConfiguration<OrganisationExportCommandReceipt>
{
    public void Configure(EntityTypeBuilder<OrganisationExportCommandReceipt> builder)
    {
        builder.ToTable("OrganisationExportInbox");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OrganisationId, x.MessageId }).IsUnique();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResponseType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResponseJson).HasColumnType("jsonb").IsRequired();
    }
}

public sealed class OrganisationExportOutboxMessageConfiguration : IEntityTypeConfiguration<OrganisationExportOutboxMessage>
{
    public void Configure(EntityTypeBuilder<OrganisationExportOutboxMessage> builder)
    {
        builder.ToTable("OrganisationExportOutbox");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OrganisationId, x.MessageId }).IsUnique();
        builder.Property(x => x.MessageType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.PayloadSha256).HasMaxLength(64).IsRequired();
    }
}
