using ClientManagement.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations;

public sealed class OrganisationClosureFenceConfiguration : IEntityTypeConfiguration<OrganisationClosureFence>
{
    public void Configure(EntityTypeBuilder<OrganisationClosureFence> builder)
    {
        builder.ToTable("OrganisationClosureFences");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OperationId, x.OrganisationId }).IsUnique();
        builder.HasIndex(x => x.OrganisationId).IsUnique().HasFilter("\"ReleasedAt\" IS NULL");
        builder.Ignore(x => x.State);
        builder.Property(x => x.ParticipantId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FenceToken).HasMaxLength(200).IsRequired();
        builder.Property(x => x.FenceRevision).IsConcurrencyToken();
    }
}

public sealed class OrganisationClosureCommandReceiptConfiguration
    : IEntityTypeConfiguration<OrganisationClosureCommandReceipt>
{
    public void Configure(EntityTypeBuilder<OrganisationClosureCommandReceipt> builder)
    {
        builder.ToTable("OrganisationClosureInbox");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OrganisationId, x.MessageId }).IsUnique();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.ResponseType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ResponseJson).HasColumnType("jsonb").IsRequired();
    }
}

public sealed class OrganisationClosureOutboxMessageConfiguration
    : IEntityTypeConfiguration<OrganisationClosureOutboxMessage>
{
    public void Configure(EntityTypeBuilder<OrganisationClosureOutboxMessage> builder)
    {
        builder.ToTable("OrganisationClosureOutbox");
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => new { x.OrganisationId, x.MessageId }).IsUnique();
        builder.Property(x => x.MessageType).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.PayloadSha256).HasMaxLength(64).IsRequired();
    }
}
