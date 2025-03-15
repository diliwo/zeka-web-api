using AuthManagement.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManagement.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Username)
            .IsRequired();

        builder.Property(u => u.Password)
            .IsRequired();

        builder.Property(u => u.Role)
            .IsRequired();

        builder.HasData(
            new User
            {
                Id = Guid.NewGuid(),
                Username = "jhon@zeka.com",
                Password = "oKNrqkO7iC#G",
                Role = "Administrator"
            });
    }
}