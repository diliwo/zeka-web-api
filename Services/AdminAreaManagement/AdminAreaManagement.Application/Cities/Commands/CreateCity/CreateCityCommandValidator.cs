using AdminAreaManagement.Core.Entities;
using FluentValidation;

namespace AdminAreaManagement.Application.Cities.Commands.CreateCity;

public class CreateCityCommandValidator : AbstractValidator<CreateCityCommand>
{
    public CreateCityCommandValidator(ICityQueries cities)
    {
        RuleFor(v => v.Name).Custom((value, context) => ValidateText(value, "Name", context));
        RuleFor(v => v.Country).Custom((value, context) => ValidateText(value, "Country", context));

        RuleFor(v => v.Name)
            .MustAsync(async (command, _, cancellationToken) =>
                !await cities.ActiveCityExistsAsync(command.Name, command.Country, cancellationToken))
            .When(command => CityText.IsValid(command.Name) && CityText.IsValid(command.Country))
            .WithMessage("The specified city already exists.");
    }

    private static void ValidateText(string value, string field, ValidationContext<CreateCityCommand> context)
    {
        if (!CityText.TryNormalize(value, out var normalized))
            context.AddFailure(value is null ? $"{field} is required." : $"{field} must be valid Unicode text.");
        else if (normalized.Length == 0)
            context.AddFailure($"{field} is required.");
        else if (CityText.ScalarLength(normalized) > CityText.MaximumLength)
            context.AddFailure($"{field} must not exceed {CityText.MaximumLength} Unicode scalar values after normalization and trimming.");
    }
}
