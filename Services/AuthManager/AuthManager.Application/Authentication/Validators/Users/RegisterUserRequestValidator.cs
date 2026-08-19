using AuthManager.Application.Authentication.Models;
using FluentValidation;

namespace AuthManager.Application.Common.Validators.Users;

public class RegisterUserRequestValidator : AbstractValidator<RegisterUserRequest>
{
    public RegisterUserRequestValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty()
            .EmailAddress()
            .MaximumLength(256);

        RuleFor(request => request.UserName)
            .NotEmpty()
            .MaximumLength(256);

        RuleFor(request => request.FirstName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(request => request.LastName)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(request => request.Password)
            .NotEmpty();
    }
}
