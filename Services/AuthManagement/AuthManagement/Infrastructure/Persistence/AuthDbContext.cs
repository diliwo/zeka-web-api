using AuthManagement.Infrastructure.Interfaces;
using AuthManagement.Infrastructure.Persistence.Configurations;
using AuthManagement.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthManagement.Infrastructure.Persistence;

public class AuthDbContext : DbContext, IAuthStore
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UserConfiguration());
    }

    public async Task<User?> VerifyUserLogin(string username, string password) => await Users.FirstOrDefaultAsync(u =>
        u.Username == username &&
        u.Password == password);
}