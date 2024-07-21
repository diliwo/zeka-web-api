using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class NatureOfContractConfiguration : IEntityTypeConfiguration<NatureOfContract>
    {
        public void Configure(EntityTypeBuilder<NatureOfContract> builder)
        {
            builder
                .Property(s => s.Id).HasColumnName("NatureOfContractId");
            builder
                .HasKey(r => new { r.Id });
        }
    }
}