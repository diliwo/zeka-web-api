using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("OutboxMessages");
        builder.HasKey(value => value.Id);
        builder.Property(value => value.MessageType).HasMaxLength(300).IsRequired();
        builder.Property(value => value.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(value => value.CorrelationId).HasMaxLength(200).IsRequired();
        builder.Property(value => value.LastError).HasMaxLength(2000);
        builder.HasIndex(value => new { value.ProcessedAtUtc, value.DeadLetteredAtUtc, value.NextAttemptAtUtc });
        builder.HasIndex(value => value.LeaseExpiresAtUtc);
        builder.HasIndex(value => value.CorrelationId);
    }
}
