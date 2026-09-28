using AdminAreaManagement.Infrastructure.Persistence.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations;

public sealed class AdminAreaExportFenceConfiguration : IEntityTypeConfiguration<AdminAreaExportFence>
{
    public void Configure(EntityTypeBuilder<AdminAreaExportFence> builder)
    {
        builder.ToTable("AdminAreaExportFences");
        builder.HasKey(x => new { x.OperationId, x.OrganisationId });
        builder.Property(x => x.FenceToken).HasMaxLength(96).IsRequired();
        builder.HasIndex(x => x.OrganisationId).IsUnique().HasFilter("\"ReleasedAt\" IS NULL");
        builder.HasIndex(x => new { x.OrganisationId, x.FenceToken }).IsUnique();
    }
}

public sealed class AdminAreaExportInboxConfiguration : IEntityTypeConfiguration<AdminAreaExportInbox>
{
    public void Configure(EntityTypeBuilder<AdminAreaExportInbox> builder)
    {
        builder.ToTable("AdminAreaExportInbox");
        builder.HasKey(x => new { x.MessageId, x.OrganisationId });
        builder.Property(x => x.CommandType).HasMaxLength(96).IsRequired();
        builder.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
    }
}

public sealed class AdminAreaExportOutboxConfiguration : IEntityTypeConfiguration<AdminAreaExportOutbox>
{
    public void Configure(EntityTypeBuilder<AdminAreaExportOutbox> builder)
    {
        builder.ToTable("AdminAreaExportOutbox");
        builder.HasKey(x => new { x.MessageId, x.OrganisationId });
        builder.Property(x => x.MessageType).HasMaxLength(160).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
    }
}

public sealed class AdminAreaExportFragmentConfiguration : IEntityTypeConfiguration<AdminAreaExportFragment>
{
    public void Configure(EntityTypeBuilder<AdminAreaExportFragment> builder)
    {
        builder.ToTable("AdminAreaExportFragments");
        builder.HasKey(x => new { x.OperationId, x.OrganisationId, x.ParticipantId });
        builder.Property(x => x.ParticipantId).HasMaxLength(96).IsRequired();
        builder.Property(x => x.FenceToken).HasMaxLength(96).IsRequired();
        builder.Property(x => x.FragmentHash).HasMaxLength(64).IsRequired();
        builder.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
    }
}
