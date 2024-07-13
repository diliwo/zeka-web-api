using Cpas.Isp.Domain.Referents.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cpas.Isp.Domain.Beneficiaries.Entities;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cpas.Isp.Infrastructure.Persistence.Configurations
{
    public class ContratPiisConfiguration : IEntityTypeConfiguration<ContratPiis>
    {
        public void Configure(EntityTypeBuilder<ContratPiis> builder)
        {
            builder.Property(l => l.Id).HasColumnName("ContratPiisId");
        }
    }
}
