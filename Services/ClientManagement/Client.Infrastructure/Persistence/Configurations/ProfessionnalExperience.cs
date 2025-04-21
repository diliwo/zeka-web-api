using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class ProfessionnalExperienceConfiguration : IEntityTypeConfiguration<ProfessionnalExperience>
    {
        public void Configure(EntityTypeBuilder<ProfessionnalExperience> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("ProfessionnalExperienceId");
            builder
                .HasOne(s => s.Client)
                .WithMany(r => r.ProfessionnalExpectations)
                .HasForeignKey(e => e.ClientId);
            builder
                .HasQueryFilter(c => c.Softdelete);
            builder
                .HasQueryFilter(a => !a.Client.Softdelete);
        }
    }
}