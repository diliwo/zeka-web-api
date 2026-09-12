using AdminAreaManagement.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations;

public sealed class PartnerContactConfiguration : IEntityTypeConfiguration<ContactPerson>
{
    public void Configure(EntityTypeBuilder<ContactPerson> builder)
    {
        builder.ToTable("ContactPersons");
        builder.HasKey(p => new { p.PartnerId, p.OrganisationId, p.ContactDetails, p.Gender, p.ToDelete });
        builder.Property(p => p.ContactName).IsRequired();
    }
}

public sealed class PartnerEmailConfiguration : IEntityTypeConfiguration<Email>
{
    public void Configure(EntityTypeBuilder<Email> builder)
    {
        builder.ToTable("Emails");
        builder.HasKey(p => new { p.PartnerId, p.OrganisationId, p.Id });
        builder.Property(p => p.Id).ValueGeneratedOnAdd();
        builder.Property(p => p.EmailAddress).IsRequired();
    }
}
