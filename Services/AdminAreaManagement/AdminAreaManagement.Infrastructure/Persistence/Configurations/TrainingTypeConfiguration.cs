using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class TrainingTypeConfiguration : IEntityTypeConfiguration<TrainingType>
    {
        public void Configure(EntityTypeBuilder<TrainingType> builder)
        {
            builder
                .Property(s => s.Id).HasColumnName("TrainingTypeId");
            builder
                .HasKey(r => new { r.Id });
        }
    }
}