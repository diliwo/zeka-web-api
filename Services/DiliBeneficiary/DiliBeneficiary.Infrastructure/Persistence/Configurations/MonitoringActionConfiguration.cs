using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class MonitoringActionConfiguration : IEntityTypeConfiguration<MonitoringAction>
    {
        public void Configure(EntityTypeBuilder<MonitoringAction> builder)
        {
            builder.ToTable("MonitoringActions");
            builder.Property(m => m.Id).HasColumnName("ActionId");
            builder.HasKey(m => new { m.Id });
            builder.Property(m => m.Action)
                .IsRequired()
                .HasMaxLength(30);
        }
    }
}
