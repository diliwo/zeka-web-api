using AuthManager.Core.Models.Users;
using FluentValidation;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Application.Common.Validators.Users;

public class RegisterUserRequestValidator : AbstractValidator<RegisterUserRequest>
{
    private readonly RoleManager<IdentityRole> _roleManager;
    public RegisterUserRequestValidator(RoleManager<IdentityRole> roleManager)
	{
        _roleManager = roleManager;

		RuleFor(request => request.Roles)
			.NotEmpty().WithMessage("Roles are required.")
            .DependentRules(() =>
             {
                 RuleFor(request => request.Roles)
                     .CustomAsync(async (roles, context, cancellationToken) =>
                     {
                         foreach (var role in roles
                             .Where(role => !string.IsNullOrWhiteSpace(role))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                         {
                             if (!await roleManager.RoleExistsAsync(role))
                             {
                                 context.AddFailure(
                                     nameof(RegisterUserRequest.Roles),
                                     $"Role '{role}' does not exist.");
                             }
                         }
                     });
             });
    }
}
