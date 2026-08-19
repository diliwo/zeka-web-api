using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Action).HasMaxLength(160).IsRequired();
        builder.Property(value => value.SubjectType).HasMaxLength(120).IsRequired();
        builder.Property(value => value.SubjectId).HasMaxLength(200).IsRequired();
        builder.Property(value => value.Outcome).HasMaxLength(80).IsRequired();
        builder.Property(value => value.CorrelationId).HasMaxLength(200).IsRequired();
        builder.Property(value => value.Metadata).HasColumnType("jsonb").IsRequired();
        builder.HasIndex(value => new { value.OrganisationId, value.OccurredAtUtc });
        builder.HasIndex(value => value.CorrelationId);
    }
}
