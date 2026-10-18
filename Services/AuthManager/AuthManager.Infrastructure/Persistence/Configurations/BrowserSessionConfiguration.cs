using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Persistence.Configurations;

internal static class BrowserSessionConfiguration
{
    public static void Configure(ModelBuilder model)
    {
        var families = model.Entity<BrowserSessionFamily>();
        families.ToTable("BrowserSessionFamilies");
        families.HasKey(x => x.Id);
        families.Property(x => x.SecurityStamp).HasMaxLength(256).IsRequired();
        families.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        families.HasIndex(x => x.UserId);
        var credentials = model.Entity<BrowserRefreshCredential>();
        credentials.ToTable("BrowserRefreshCredentials");
        credentials.HasKey(x => x.Hash);
        credentials.Property(x => x.Hash).HasMaxLength(64);
        credentials.HasOne<BrowserSessionFamily>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
        credentials.HasIndex(x => x.FamilyId);
    }
}
