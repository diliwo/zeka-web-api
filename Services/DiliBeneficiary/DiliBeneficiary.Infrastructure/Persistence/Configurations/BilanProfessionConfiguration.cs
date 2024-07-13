using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class BilanProfessionConfiguration : IEntityTypeConfiguration<BilanProfession>
    {
        public void Configure(EntityTypeBuilder<BilanProfession> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("BilanProfessionId");
            builder
                .HasKey(r => new { r.Id });
            builder
                .HasOne(s => s.Bilan)
                .WithMany(r => r.BilanProfessions)
                .HasForeignKey(e => e.BilanId);
        }
    }
}