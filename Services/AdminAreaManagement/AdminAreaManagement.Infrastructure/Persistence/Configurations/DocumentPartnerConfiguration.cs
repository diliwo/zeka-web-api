using AdminAreaManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminAreaManagement.Infrastructure.Persistence.Configurations
{
    class DocumentPartnerConfiguration  : IEntityTypeConfiguration<DocumentPartner>
    {
        public void Configure(EntityTypeBuilder<DocumentPartner> builder)
        {
            builder.HasKey(r => new { r.Id });
            builder
                .HasOne(d => d.Partner)
                .WithMany(j => j.Documents)
                .HasForeignKey(f => f.PartnerId);
            builder
                .Ignore(d => d.ContentFile);
        }
    }
}
