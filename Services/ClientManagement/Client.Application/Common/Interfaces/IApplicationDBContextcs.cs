using Client.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Client.Application.Common.Interfaces
{
    public interface IApplicationDbContext
    {
        public DbSet<Core.Entities.Client> Clients { get; set; }
        public DbSet<Track> Supports { get; set; }
        public DbSet<Staff> Staffs { get; set; }
        public DbSet<Service> Services { get; set; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    }
}
