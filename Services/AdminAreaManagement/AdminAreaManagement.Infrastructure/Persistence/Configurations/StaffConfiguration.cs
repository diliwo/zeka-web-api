using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class StaffMemberConfiguration : IEntityTypeConfiguration<StaffMember>
    {
        public void Configure(EntityTypeBuilder<StaffMember> builder)
        {
            builder.HasKey(r => new { r.Id });
            builder.Property(r => r.FirstName)
                .IsRequired()
                .HasMaxLength(20);

            builder.Property(r => r.LastName)
                .IsRequired()
                .HasMaxLength(20);

            builder.Property(r => r.UserName)
                .HasMaxLength(60);
        }
    }
}