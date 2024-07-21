using DiliBeneficiary.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Application.Common.Interfaces
{
    public interface IApplicationDbContext
    {
        public DbSet<Beneficiary> Beneficiaries { get; set; }
        public DbSet<Support> Supports { get; set; }
        public DbSet<Referent> Referents { get; set; }
        public DbSet<Service> Services { get; set; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    }
}
