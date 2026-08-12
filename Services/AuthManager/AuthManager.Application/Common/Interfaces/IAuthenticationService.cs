using AuthManager.Core.Models.Tokens;
using AuthManager.Core.Models.Users;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Application.Common.Interfaces;

public interface IAuthenticationService
{
	Task<IdentityResult> RegisterUserAsync(RegisterUserRequest request);
}
