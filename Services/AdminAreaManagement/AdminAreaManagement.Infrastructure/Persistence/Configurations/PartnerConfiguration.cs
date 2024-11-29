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
            builder
                .HasKey(r => new { r.Id });
            builder
                .OwnsOne(p => p.Address);
            builder
                .OwnsMany<Phone>("Phones", t =>
                {
                    t.WithOwner().HasForeignKey("PartnerId");
                    t.Property(p => p.PhoneNumber);
                    t.Property(p => p.ContactName);
                    t.Property(p => p.Gender);
                    t.Property(p => p.ToDelete);
                    t.HasKey("PartnerId","PhoneNumber","Gender", "ToDelete");
                    t.ToTable("Phones");
                });
            builder
                .OwnsMany<Email>(e => e.Emails, a =>
                {
                    a.WithOwner().HasForeignKey("PartnerId");
                    a.Property(e => e.EmailAddress);
                    a.HasKey("PartnerId","Id");
                    a.ToTable("Emails");
                });            
            builder
                .Property(e => e.DateOfAgreementSignature).HasColumnType("date");
            builder
                .Ignore( r =>r.PhoneNumbers);
        }
    }
}