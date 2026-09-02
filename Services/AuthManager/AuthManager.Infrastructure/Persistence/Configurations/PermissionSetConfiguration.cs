using AuthManager.Core.Organisations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

public sealed class PermissionSetConfiguration : IEntityTypeConfiguration<PermissionSet>
{
    public void Configure(EntityTypeBuilder<PermissionSet> builder)
    {
        builder.ToTable("PermissionSets");
        builder.HasKey(permissionSet => permissionSet.Id);
        builder.Property(permissionSet => permissionSet.Code).HasMaxLength(100).IsRequired();
        builder.Property(permissionSet => permissionSet.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(permissionSet => permissionSet.IsSystem).IsRequired();
        builder.HasIndex(permissionSet => permissionSet.Code).IsUnique();
        builder.HasData(PermissionSet.SystemPermissionSets);
    }
}
