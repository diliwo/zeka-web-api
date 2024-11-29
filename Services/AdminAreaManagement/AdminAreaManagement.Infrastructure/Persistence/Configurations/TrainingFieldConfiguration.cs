using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class TrainingFieldConfiguration : IEntityTypeConfiguration<TrainingField>
    {
        public void Configure(EntityTypeBuilder<TrainingField> builder)
        {
            builder
                .Property(s => s.Id).HasColumnName("TrainingFieldId");
            builder
                .HasKey(r => new { r.Id });
        }
    }
}