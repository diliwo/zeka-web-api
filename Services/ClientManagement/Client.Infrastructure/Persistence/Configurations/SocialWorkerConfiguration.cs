using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations;

public class SocialWorkerConfiguration : IEntityTypeConfiguration<SocialWorker>
{
    public void Configure(EntityTypeBuilder<SocialWorker> builder)
    {
        builder.Property(r => r.Id).HasColumnName("SocialWorkerId");
        builder.HasKey(r => new { r.Id });
        builder.Property(r => r.FirstName)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(r => r.LastName)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(r => r.UserName)
            .HasMaxLength(60);
        builder.HasIndex(r => new { r.OrganisationId, r.UserName }).IsUnique();
    }
}
