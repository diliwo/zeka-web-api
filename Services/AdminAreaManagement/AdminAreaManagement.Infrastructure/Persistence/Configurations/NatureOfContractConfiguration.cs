using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class NatureOfContractConfiguration : IEntityTypeConfiguration<NatureOfContract>
    {
        public void Configure(EntityTypeBuilder<NatureOfContract> builder)
        {
            builder
                .HasKey(r => new { r.Id });
        }
    }
}