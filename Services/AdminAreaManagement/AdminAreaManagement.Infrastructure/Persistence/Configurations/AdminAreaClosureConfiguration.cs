using AdminAreaManagement.Infrastructure.Persistence.Closures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations;

public sealed class AdminAreaClosureFenceConfiguration : IEntityTypeConfiguration<AdminAreaClosureFence>
{
    public void Configure(EntityTypeBuilder<AdminAreaClosureFence> builder)
    {
        builder.ToTable("AdminAreaClosureFences");
        builder.HasKey(x => new { x.OperationId, x.OrganisationId, x.ParticipantId });
        builder.Property(x => x.ParticipantId).HasMaxLength(96).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.FenceToken).HasMaxLength(96).IsRequired();
        builder.HasIndex(x => new { x.OrganisationId, x.ParticipantId }).IsUnique()
            .HasFilter("\"ReleasedAt\" IS NULL");
        builder.HasIndex(x => new { x.OrganisationId, x.ParticipantId, x.FenceToken }).IsUnique();
    }
}

public sealed class AdminAreaClosureInboxConfiguration : IEntityTypeConfiguration<AdminAreaClosureInbox>
{
    public void Configure(EntityTypeBuilder<AdminAreaClosureInbox> builder)
    {
        builder.ToTable("AdminAreaClosureInbox");
        builder.HasKey(x => new { x.MessageId, x.OrganisationId });
        builder.Property(x => x.ParticipantId).HasMaxLength(96).IsRequired();
        builder.Property(x => x.CommandType).HasMaxLength(96).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
    }
}

public sealed class AdminAreaClosureOutboxConfiguration : IEntityTypeConfiguration<AdminAreaClosureOutbox>
{
    public void Configure(EntityTypeBuilder<AdminAreaClosureOutbox> builder)
    {
        builder.ToTable("AdminAreaClosureOutbox");
        builder.HasKey(x => new { x.MessageId, x.OrganisationId });
        builder.Property(x => x.ParticipantId).HasMaxLength(96).IsRequired();
        builder.Property(x => x.MessageType).HasMaxLength(160).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
    }
}
