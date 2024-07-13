using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DiliBeneficiary.Infrastructure.Persistence.Configurations
{
    public class AnnualClosureHandlingHistoryConfiguration : IEntityTypeConfiguration<AnnualClosureHandlingHistory>
    {
        public void Configure(EntityTypeBuilder<AnnualClosureHandlingHistory> builder)
        {
            builder.ToTable("AnnualClosuresHandlingHistory");
            builder.HasKey(m => new {m.ClosureStartDate, m.Niss});
        }
    }
}
