using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class PartnerConfiguration : IEntityTypeConfiguration<Partner>
    {
        public void Configure(EntityTypeBuilder<Partner> builder)
        {
            builder.HasKey(r => new { r.Id });
            builder
                .Property(p => p.Name).IsRequired();
            builder.HasIndex(p => new { p.OrganisationId, p.PartnerNumber }).IsUnique();
            builder.HasOne(p => p.StaffMember)
                .WithMany()
                .HasForeignKey(p => new { p.StaffMemberId, p.OrganisationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganisationId });
            builder
                .HasKey(r => new { r.Id });
            builder.Ignore(p => p.Address);
            foreach (var part in new[] { "Number", "Street", "PostalCode", "City" })
                builder.Property<string>("Address" + part).HasColumnName("Address_" + part).IsRequired();
            builder.HasMany(p => p.ContactPersons).WithOne()
                .HasForeignKey(p => new { p.PartnerId, p.OrganisationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganisationId }).OnDelete(DeleteBehavior.Cascade);
            builder.HasMany(p => p.Emails).WithOne()
                .HasForeignKey(p => new { p.PartnerId, p.OrganisationId })
                .HasPrincipalKey(p => new { p.Id, p.OrganisationId }).OnDelete(DeleteBehavior.Cascade);
            builder
                .Property(e => e.DateOfAgreementSignature).HasColumnType("date");
            builder
                .Ignore( r =>r.Contacts);
        }
    }
}
