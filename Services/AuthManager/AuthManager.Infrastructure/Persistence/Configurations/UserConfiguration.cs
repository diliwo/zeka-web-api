using AuthManager.Core.Models.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthManager.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        //builder.HasData(
        //    new User
        //    {
        //        Id = Guid.NewGuid().ToString(),
        //        UserName = "jhon@zeka.com",
        //        PasswordHash = "oKNrqkO7iC#G",
        //        R = "Administrator"
        //    });
    }
}