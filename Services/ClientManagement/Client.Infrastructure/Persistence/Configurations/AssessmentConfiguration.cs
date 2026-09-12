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
            builder.Ignore(c => c.Professions);
            builder.HasOne(c => c.Client).WithMany()
                .HasForeignKey(c => new { c.ClientId, c.OrganisationId })
                .HasPrincipalKey(c => new { c.Id, c.OrganisationId });
        }
    }
}
