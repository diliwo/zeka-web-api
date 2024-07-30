using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class ContratPiisConfiguration : IEntityTypeConfiguration<ContratPiis>
    {
        public void Configure(EntityTypeBuilder<ContratPiis> builder)
        {
            builder.Property(l => l.Id).HasColumnName("ContratPiisId");
        }
    }
}
