using AuthManager.Core.Models.Users;
using FluentValidation;

namespace AuthManager.Application.Common.Validators.Users;

public class LoginUserRequestValidator : AbstractValidator<LoginUserRequest>
{
	public LoginUserRequestValidator()
	{
		RuleFor(request => request.UserNameOrEmail)
			.NotEmpty().WithMessage("Username or email is required.");

		RuleFor(request => request.Password)
			.NotEmpty().WithMessage("Password is required.")
			.MinimumLength(8).WithMessage("Password must be at least 8 characters long.");
	}
}
