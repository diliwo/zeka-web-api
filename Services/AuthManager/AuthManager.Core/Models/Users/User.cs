using Microsoft.AspNetCore.Identity;

namespace AuthManager.Core.Models.Users;

public class User : IdentityUser
{
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public UserStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? UpdatedAtUct { get; set; }
}