using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class BilanConfiguration : IEntityTypeConfiguration<Bilan>
    {
        public void Configure(EntityTypeBuilder<Bilan> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("BilanId");
            builder
                .HasOne(s => s.Beneficiary)
                .WithMany(r => r.Bilans)
                .HasForeignKey(e => e.BeneficiaryId);
            builder.Ignore(c => c.Professions);
        }
    }
}