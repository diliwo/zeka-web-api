using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class AnnualClosureConfiguration : IEntityTypeConfiguration<AnnualClosure>
    {
        public void Configure(EntityTypeBuilder<AnnualClosure> builder)
        {
            builder.ToTable("AnnualClosures");
            builder.Property(a => a.Id).HasColumnName("AnnualClosureId");
            builder.HasKey(m => new { m.Id });
            builder.Property(m => m.StartDay)
                .IsRequired();
            builder.Property(m => m.StartMonth)
                .IsRequired();
            builder.Property(m => m.EndDay)
                .IsRequired();
            builder.Property(m => m.EndMonth)
                .IsRequired();
            builder.HasOne(a => a.Job)
                .WithMany(j => j.AnnualClosures)
                .HasForeignKey(a => a.JobId);
            builder.HasQueryFilter(a => !a.Softdelete);

        }
    }
}
