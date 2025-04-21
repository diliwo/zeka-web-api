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
                .HasForeignKey(e => e.ClientId);
            builder
                .HasQueryFilter(c => c.Softdelete);
            builder
                .HasQueryFilter(a => !a.Client.Softdelete);
        }
    }
}