using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal sealed class TimeZoneConfigurationAuditConfiguration : IEntityTypeConfiguration<TimeZoneConfigurationAudit>
{
    public void Configure(EntityTypeBuilder<TimeZoneConfigurationAudit> builder)
    {
        builder.ToTable("TimeZoneConfigurationAudits");
        builder.HasKey(x => x.ApprovalId);
        builder.Property(x => x.ArtifactSha256).HasMaxLength(64).IsRequired();
        builder.Property(x => x.OwnerKeyId).HasMaxLength(120).IsRequired();
        builder.Property(x => x.ExecutorSubject).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ReviewerSubject).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ReleaseEvidenceId).HasMaxLength(200).IsRequired();
        builder.Property(x => x.ChangeReference).HasMaxLength(200).IsRequired();
        builder.Property(x => x.PreviousTimeZoneId).HasMaxLength(128);
        builder.Property(x => x.TimeZoneId).HasMaxLength(128).IsRequired();
        builder.Property(x => x.CorrelationId).HasMaxLength(200).IsRequired();
        builder.HasIndex(x => new { x.OrganisationId, x.OccurredAtUtc });
    }
}
