using AuthManager.Core.Organisations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

public sealed class MembershipPermissionGrantConfiguration
    : IEntityTypeConfiguration<MembershipPermissionGrant>
{
    public void Configure(EntityTypeBuilder<MembershipPermissionGrant> builder)
    {
        builder.ToTable("MembershipPermissionGrants");
        builder.HasKey(grant => grant.Id);
        builder.Property(grant => grant.OrganisationId).IsRequired();
        builder.Property(grant => grant.OrganisationMembershipId).IsRequired();
        builder.Property(grant => grant.PermissionKey).HasMaxLength(160).IsRequired();
        builder.Property(grant => grant.GrantedByMembershipId).IsRequired();
        builder.Property(grant => grant.GrantedBySubjectId).IsRequired();
        builder.Property(grant => grant.GrantedAtUtc).IsRequired();
        builder.Property(grant => grant.ConcurrencyVersion).IsConcurrencyToken().IsRequired();
        builder.Ignore(grant => grant.IsActive);

        builder.HasIndex(grant => new { grant.OrganisationMembershipId, grant.PermissionKey })
            .IsUnique()
            .HasFilter("\"RevokedAtUtc\" IS NULL");
        builder.HasIndex(grant => new { grant.OrganisationId, grant.GrantedAtUtc });

        builder.HasOne<OrganisationMembership>().WithMany()
            .HasForeignKey(grant => new { grant.OrganisationMembershipId, grant.OrganisationId })
            .HasPrincipalKey(membership => new { membership.Id, membership.OrganisationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrganisationMembership>().WithMany()
            .HasForeignKey(grant => new { grant.GrantedByMembershipId, grant.OrganisationId })
            .HasPrincipalKey(membership => new { membership.Id, membership.OrganisationId })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrganisationMembership>().WithMany()
            .HasForeignKey(grant => new { grant.RevokedByMembershipId, grant.OrganisationId })
            .HasPrincipalKey(membership => new { membership.Id, membership.OrganisationId })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
