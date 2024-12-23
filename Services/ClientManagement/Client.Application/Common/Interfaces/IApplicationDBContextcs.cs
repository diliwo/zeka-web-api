using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Application.Common.Interfaces
{
    public interface IApplicationDbContext
    {
        public DbSet<Client> Clients { get; set; }
        public DbSet<Track> Tracks { get; set; }
        public DbSet<StaffMember> StaffMembers { get; set; }
        public DbSet<Team> Teams { get; set; }

        Task<int> SaveChangesAsync(CancellationToken cancellationToken);
    }
}
