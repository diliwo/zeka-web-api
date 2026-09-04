using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

public sealed class OrganisationConfiguration : IEntityTypeConfiguration<Organisation>
{
    public void Configure(EntityTypeBuilder<Organisation> builder)
    {
        builder.ToTable("Organisations");
        builder.HasKey(organisation => organisation.Id);
        builder.Property(organisation => organisation.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(organisation => organisation.OwnerUserId).IsRequired();
        builder.Property(organisation => organisation.Status).IsRequired();
        builder.Property(organisation => organisation.Slug).HasMaxLength(200);
        builder.Property(organisation => organisation.CreatedAtUtc).IsRequired();
        builder.Property(organisation => organisation.UpdatedAtUtc).IsRequired();
        builder.Property(organisation => organisation.ConcurrencyVersion).IsConcurrencyToken().IsRequired();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(organisation => organisation.OwnerUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
