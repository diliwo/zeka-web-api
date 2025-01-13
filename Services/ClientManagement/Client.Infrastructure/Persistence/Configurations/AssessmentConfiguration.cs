using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class AssessmentConfiguration : IEntityTypeConfiguration<Assessment>
    {
        public void Configure(EntityTypeBuilder<Assessment> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("AssessmentId");
            builder
                .HasOne(s => s.Client)
                .WithMany(r => r.Assessments)
                .HasForeignKey(e => e.ClientId);
            builder.Ignore(c => c.Professions);
        }
    }
}