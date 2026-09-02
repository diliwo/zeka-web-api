using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

public sealed class OrganisationMembershipConfiguration : IEntityTypeConfiguration<OrganisationMembership>
{
    public void Configure(EntityTypeBuilder<OrganisationMembership> builder)
    {
        builder.ToTable("OrganisationMemberships");
        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.OrganisationId).IsRequired();
        builder.Property(membership => membership.UserId).IsRequired();
        builder.Property(membership => membership.PermissionSetId).IsRequired();
        builder.Property(membership => membership.Status).IsRequired();
        builder.Property(membership => membership.JoinedAtUtc).IsRequired();
        builder.Property(membership => membership.ConcurrencyVersion).IsConcurrencyToken().IsRequired();

        builder.HasIndex(membership => new { membership.UserId, membership.OrganisationId }).IsUnique();
        builder.HasOne<Organisation>().WithMany().HasForeignKey(membership => membership.OrganisationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PermissionSet>().WithMany().HasForeignKey(membership => membership.PermissionSetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
