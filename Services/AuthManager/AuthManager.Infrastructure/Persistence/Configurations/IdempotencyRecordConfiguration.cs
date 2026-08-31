using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("IdempotencyRecords");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.Operation).HasMaxLength(160).IsRequired();
        builder.Property(value => value.KeyHash).HasMaxLength(64).IsRequired();
        builder.Property(value => value.RequestHash).HasMaxLength(64).IsRequired();
        builder.Property(value => value.SafeResponse).HasColumnType("jsonb");
        builder.HasIndex(value => new { value.UserId, value.Operation, value.KeyHash }).IsUnique();
        builder.HasIndex(value => value.RetainUntilUtc);
    }
}
