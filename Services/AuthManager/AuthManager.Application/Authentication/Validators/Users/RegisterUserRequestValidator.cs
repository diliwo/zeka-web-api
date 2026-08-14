using AuthManager.Core.Models.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Application.Common.Validators.Users;

public class RegisterUserRequestValidator : AbstractValidator<RegisterUserRequest>
{
    private readonly RoleManager<Role> _roleManager;
    public RegisterUserRequestValidator(RoleManager<Role> roleManager)
	{
        _roleManager = roleManager;
    }
}
