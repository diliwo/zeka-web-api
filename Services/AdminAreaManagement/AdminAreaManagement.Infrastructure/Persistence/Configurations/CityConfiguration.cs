using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class CityConfiguration : IEntityTypeConfiguration<City>
    {
        public void Configure(EntityTypeBuilder<City> builder)
        {
            builder
                .Property(s => s.Id).HasColumnName("CityId");
            builder
                .HasKey(r => new { r.Id });

            // Display values remain text: the limit applies after NFC/trim, not to stored UTF-16 length.
            builder.Property(city => city.Name).IsRequired();
            builder.Property(city => city.Country).IsRequired();
            builder.Property<string>("NormalizedName").IsRequired().UseCollation("C")
                .HasComputedColumnSql("public.zeka_city_key(\"Name\")", stored: true);
            builder.Property<string>("NormalizedCountry").IsRequired().UseCollation("C")
                .HasComputedColumnSql("public.zeka_city_key(\"Country\")", stored: true);
            builder.HasIndex("NormalizedName", "NormalizedCountry")
                .HasDatabaseName("UX_Cities_ActiveNormalizedIdentity")
                .IsUnique().HasFilter("NOT \"Softdelete\"");
            builder.ToTable("Cities", table =>
            {
                table.HasCheckConstraint("CK_Cities_Name_Text",
                    "char_length(public.zeka_city_text(\"Name\")) BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_Cities_Country_Text",
                    "char_length(public.zeka_city_text(\"Country\")) BETWEEN 1 AND 100");
            });

        }
    }
}
