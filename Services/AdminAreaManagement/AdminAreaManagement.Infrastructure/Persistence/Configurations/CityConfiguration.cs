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

            // PostgreSQL varchar counts Unicode scalars; Domain canonicalizes before persistence.
            builder.Property(city => city.Name).IsRequired().HasMaxLength(CityText.MaximumLength);
            builder.Property(city => city.Country).IsRequired().HasMaxLength(CityText.MaximumLength);
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
                    "\"Name\" COLLATE \"C\" = public.zeka_city_text(\"Name\") COLLATE \"C\" AND char_length(\"Name\") BETWEEN 1 AND 100");
                table.HasCheckConstraint("CK_Cities_Country_Text",
                    "\"Country\" COLLATE \"C\" = public.zeka_city_text(\"Country\") COLLATE \"C\" AND char_length(\"Country\") BETWEEN 1 AND 100");
            });

        }
    }
}
