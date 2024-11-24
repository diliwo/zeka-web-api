using Client.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Client.Infrastructure.Persistence.Configurations
{
    public class ProfessionalAssessmentConfiguration : IEntityTypeConfiguration<ProfessionalAssessment>
    {
        public void Configure(EntityTypeBuilder<ProfessionalAssessment> builder)
        {
            builder
                .HasKey(r => new { r.Id });
            builder
                .HasOne(s => s.Assessment)
                .WithMany(r => r.BilanProfessions)
                .HasForeignKey(e => e.AssessmentId);
        }
    }
}