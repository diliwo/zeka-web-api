using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class CityConfiguration : IEntityTypeConfiguration<City>
    {
        public void Configure(EntityTypeBuilder<City> builder)
        {
            builder
                .Property(s => s.Id).HasColumnName("CityId");
            builder
                .HasKey(r => new { r.Id });

        }
    }
}