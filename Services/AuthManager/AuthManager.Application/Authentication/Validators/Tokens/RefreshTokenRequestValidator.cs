using AuthManager.Core.Models.Tokens;
using FluentValidation;

namespace AuthManager.Application.Common.Validators.Tokens;

public class RefreshTokenRequestValidator : AbstractValidator<RefreshTokenRequest>
{
	public RefreshTokenRequestValidator()
	{
		RuleFor(x => x.RefreshToken)
			.NotEmpty()
			.WithMessage("RefreshToken must not be empty.");
	}
}
