using Microsoft.AspNetCore.Identity;

namespace AuthManager.Core.Models.Users;

public class User : IdentityUser
{
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime RefreshTokenExpiresOn { get; set; }
}