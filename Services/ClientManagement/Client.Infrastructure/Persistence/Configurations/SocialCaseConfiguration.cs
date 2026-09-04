using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class SocialCaseConfiguration : IEntityTypeConfiguration<SocialCase>
    {
        public void Configure(EntityTypeBuilder<SocialCase> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("SchoolRegistrationId");
            builder
                .HasOne(s => s.Client)
                .WithMany(r => r.SocialCases)
                .HasForeignKey(e => new { e.ClientId, e.OrganisationId })
                .HasPrincipalKey(e => new { e.Id, e.OrganisationId });
            builder.HasOne(s => s.SocialWorker)
                .WithMany(r => r.SocialCases)
                .HasForeignKey(e => new { e.SocialWorkerId, e.OrganisationId })
                .HasPrincipalKey(e => new { e.Id, e.OrganisationId });
            builder
                .HasQueryFilter(c => c.Softdelete);
            builder
                .HasQueryFilter(a => !a.Client.Softdelete);
        }
    }
}
