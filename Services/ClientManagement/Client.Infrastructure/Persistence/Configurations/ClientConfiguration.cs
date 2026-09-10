using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClientManagement.Infrastructure.Persistence.Configurations
{
    public class ClientConfiguration : IEntityTypeConfiguration<ClientManagement.Core.Entities.Client>
    {
        public void Configure(EntityTypeBuilder<ClientManagement.Core.Entities.Client> builder)
        {
            builder
                .HasKey(r => new { r.Id });
            builder
                .Property(b => b.FirstName)
                .IsRequired();
            builder
                .Property(b => b.LastName)
                .IsRequired();
            builder
                .Ignore(b => b.FullName);
            builder.Property(b => b.ReferenceNumber)
                .IsRequired();
            builder.HasIndex(b => new { b.OrganisationId, b.ReferenceNumber }).IsUnique();
            builder.Property(b => b.Ssn)
                .IsRequired()
                .UsePropertyAccessMode(PropertyAccessMode.Property);
            builder.Ignore(b => b.Address);
            foreach (var part in new[] { "Number", "Street", "PostalCode", "City", "Country" })
                builder.Property<string>("Address" + part).HasColumnName("Address_" + part).IsRequired(false);
            builder.Property(b => b.NativeLanguage).HasColumnName("NativeLanguage_SpokenLanguage")
                .HasConversion(v => v == null ? null : v.SpokenLanguage,
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Language(v))
                .Metadata.SetValueComparer(LanguageComparer());
            builder.Property(b => b.ContactLanguage).HasColumnName("ContactLanguage_SpokenLanguage")
                .HasConversion(v => v == null ? null : v.SpokenLanguage,
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Language(v))
                .Metadata.SetValueComparer(LanguageComparer());
            builder.Property(b => b.Email).HasColumnName("Email_EmailAddress")
                .HasConversion(v => v == null ? null : v.EmailAddress,
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Email(v))
                .Metadata.SetValueComparer(new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<ClientManagement.Core.ValueObjects.Email?>(
                    (a, b) => (a == null ? null : a.EmailAddress) == (b == null ? null : b.EmailAddress),
                    v => v == null ? 0 : v.EmailAddress.GetHashCode(),
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Email(v.EmailAddress)));
            builder.Property(b => b.Phone).HasColumnName("Phone_PhoneNumber")
                .HasConversion(v => v == null ? null : v.PhoneNumber,
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Phone(v))
                .Metadata.SetValueComparer(PhoneComparer());
            builder.Property(b => b.MobilePhone).HasColumnName("MobilePhone_PhoneNumber")
                .HasConversion(v => v == null ? null : v.PhoneNumber,
                    v => v == null ? null : new ClientManagement.Core.ValueObjects.Phone(v))
                .Metadata.SetValueComparer(PhoneComparer());
            builder
                .Property(b => b.SocialWorkerName);
            builder
                .Property(e => e.BirthDate).HasColumnType("date");
            //builder
            //    .Ignore(b => b.Candidates);
            builder
                .Ignore(b => b.Registrations);
            builder
                .HasQueryFilter(c => !c.Softdelete);
        }
        private static Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<ClientManagement.Core.ValueObjects.Language?> LanguageComparer() => new(
            (a, b) => (a == null ? null : a.SpokenLanguage) == (b == null ? null : b.SpokenLanguage),
            v => v == null ? 0 : v.SpokenLanguage.GetHashCode(),
            v => v == null ? null : new ClientManagement.Core.ValueObjects.Language(v.SpokenLanguage));

        private static Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<ClientManagement.Core.ValueObjects.Phone?> PhoneComparer() => new(
            (a, b) => (a == null ? null : a.PhoneNumber) == (b == null ? null : b.PhoneNumber),
            v => v == null ? 0 : v.PhoneNumber.GetHashCode(),
            v => v == null ? null : new ClientManagement.Core.ValueObjects.Phone(v.PhoneNumber));
    }
}
