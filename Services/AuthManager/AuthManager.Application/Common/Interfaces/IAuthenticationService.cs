using AuthManager.Core.Models.Tokens;
using AuthManager.Core.Models.Users;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Application.Common.Interfaces;

public interface IAuthenticationService
{
	Task<TokenResponse> LoginUserAsync(LoginUserRequest request);
	Task<IdentityResult> RegisterUserAsync(RegisterUserRequest request);
	Task<TokenResponse> RefreshAccessTokenAsync(
		RefreshTokenRequest request,
		CancellationToken cancellationToken = default);
}
