using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class MonitoringReportsConfiguration : IEntityTypeConfiguration<MonitoringReport>
    {
        public void Configure(EntityTypeBuilder<MonitoringReport> builder)
        {
            builder.ToTable("MonitoringReports");
            builder.HasKey(q => new { q.Id });

            builder.HasOne(q => q.Client)
                .WithMany(b => b.MonitoringReports)
                .HasForeignKey(q => q.ClientId);

            builder.HasOne(q => q.SocialWorker)
                .WithMany(r => r.MonitoringReports)
                .HasForeignKey(q => q.SocialWorkerId);

            builder.HasOne(q => q.MonitoringAction)
                .WithMany(a => a.MonitoringReports)
                .HasForeignKey(q => q.MonitoringActionId);

            builder.Property(q => q.ActionDate)
                .HasColumnType("date")
                .IsRequired();

            builder.Property(q => q.Period)
                .UsePropertyAccessMode(PropertyAccessMode.Property)
                .HasMaxLength(6);

            builder.Property(q => q.ActionComment);
        }
    }
}
