using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Infrastructure.Identity.Models;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Infrastructure.Identity;

public sealed class IdentityAuthenticationService(
    UserManager<User> userManager,
    TimeProvider timeProvider) : IAuthenticationService
{
    public async Task<UserRegistrationResult> RegisterUserAsync(
        RegisterUserRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var user = User.Create(
            request.Email,
            request.UserName,
            request.FirstName,
            request.LastName,
            timeProvider.GetUtcNow());

        var result = await userManager.CreateAsync(user, request.Password);

        return result.Succeeded
            ? UserRegistrationResult.Success
            : UserRegistrationResult.Failure(result.Errors.Select(error => error.Description));
    }
}
