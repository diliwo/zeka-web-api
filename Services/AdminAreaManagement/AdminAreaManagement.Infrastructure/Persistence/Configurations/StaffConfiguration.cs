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
            builder.HasIndex(r => new { r.OrganisationId, r.UserName }).IsUnique();
            builder.HasOne(r => r.Team)
                .WithMany(r => r.StaffMembers)
                .HasForeignKey(r => new { r.TeamId, r.OrganisationId })
                .HasPrincipalKey(r => new { r.Id, r.OrganisationId });
        }
    }
}
