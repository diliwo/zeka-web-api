using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Common.Interfaces
{
    public interface IApplicationDbContext
    {
        public DbSet<Client> Clients { get; set; }
        public DbSet<Support> Supports { get; set; }
        public DbSet<SocialWorker> SocialWorkers { get; set; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    }
}
