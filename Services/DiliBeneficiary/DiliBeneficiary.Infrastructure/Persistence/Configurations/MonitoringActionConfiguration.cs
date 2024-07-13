using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cpas.Isp.Domain.QuarterlyMonitoring.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cpas.Isp.Infrastructure.Persistence.Configurations
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
