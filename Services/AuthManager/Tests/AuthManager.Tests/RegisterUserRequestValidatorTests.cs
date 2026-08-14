using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Validators.Users;

namespace AuthManager.Tests;

public sealed class RegisterUserRequestValidatorTests
{
    [Fact]
    public async Task Empty_names_are_rejected()
    {
        var validator = new RegisterUserRequestValidator();
        var request = new RegisterUserRequest(
            "owner@example.org",
            "owner",
            string.Empty,
            string.Empty,
            "Correct-Horse-7!");

        var result = await validator.ValidateAsync(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(request.FirstName));
        Assert.Contains(result.Errors, error => error.PropertyName == nameof(request.LastName));
    }
}
