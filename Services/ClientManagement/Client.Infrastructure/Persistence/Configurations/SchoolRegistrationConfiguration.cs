using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class SchoolRegistrationConfiguration : IEntityTypeConfiguration<SchoolRegistration>
    {
        public void Configure(EntityTypeBuilder<SchoolRegistration> builder)
        {
            builder
                .Property(r => r.Id).HasColumnName("SchoolRegistrationId");
            builder
                .HasOne(s => s.Client)
                .WithMany(r => r.SchoolRegistrations)
                .HasForeignKey(e => e.ClientId);
            builder
                .HasQueryFilter(c => c.Softdelete);
            builder
                .HasQueryFilter(a => !a.Client.Softdelete);
        }
    }
}