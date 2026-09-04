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
            builder
                .OwnsOne(p => p.Address);
            builder
                .OwnsMany<ContactPerson>("ContactPersons", t =>
                {
                    t.Property<Guid>("OrganisationId");
                    t.WithOwner()
                        .HasForeignKey("PartnerId", "OrganisationId")
                        .HasPrincipalKey(nameof(Partner.Id), nameof(Partner.OrganisationId));
                    t.Property(p => p.ContactDetails);
                    t.Property(p => p.ContactName);
                    t.Property(p => p.Gender);
                    t.Property(p => p.ToDelete);
                    t.HasKey("PartnerId", "OrganisationId", "ContactDetails", "Gender", "ToDelete");
                    t.ToTable("ContactPersons");
                });
            builder
                .OwnsMany<Email>(e => e.Emails, a =>
                {
                    a.Property<Guid>("OrganisationId");
                    a.WithOwner()
                        .HasForeignKey("PartnerId", "OrganisationId")
                        .HasPrincipalKey(nameof(Partner.Id), nameof(Partner.OrganisationId));
                    a.Property(e => e.EmailAddress);
                    a.HasKey("PartnerId", "OrganisationId", "Id");
                    a.ToTable("Emails");
                });            
            builder
                .Property(e => e.DateOfAgreementSignature).HasColumnType("date");
            builder
                .Ignore( r =>r.Contacts);
        }
    }
}
