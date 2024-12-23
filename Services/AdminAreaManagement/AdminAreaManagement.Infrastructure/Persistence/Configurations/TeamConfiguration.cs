using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    public class TeamConfiguration : IEntityTypeConfiguration<Team>
    {
        public void Configure(EntityTypeBuilder<Team> builder)
        {
            builder.HasKey(r => new { r.Id });
            builder.Property(e => e.Name).IsRequired().HasMaxLength(40);

            builder.Property(e => e.Acronym).IsRequired().HasMaxLength(7);
        }
    }
}