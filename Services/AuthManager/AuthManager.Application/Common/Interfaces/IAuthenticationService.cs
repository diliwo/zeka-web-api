using AuthManager.Application.Authentication.Models;

namespace AuthManager.Application.Common.Interfaces;

public interface IAuthenticationService
{
	Task<UserRegistrationResult> RegisterUserAsync(
		RegisterUserRequest request,
		CancellationToken cancellationToken = default);
}
