using AutoFixture;
using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Validators.Users;
using FluentAssertions;

namespace Application.UnitTests;

public sealed class RegisterUserRequestValidatorTests
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task Empty_names_are_rejected()
    {
        var validator = new RegisterUserRequestValidator();
        var request = _fixture.Build<RegisterUserRequest>()
            .With(value => value.Email, "owner@example.org")
            .With(value => value.UserName, "owner")
            .With(value => value.FirstName, string.Empty)
            .With(value => value.LastName, string.Empty)
            .With(value => value.Password, "Correct-Horse-7!")
            .Create();

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.FirstName));
        result.Errors.Should().Contain(error => error.PropertyName == nameof(request.LastName));
    }
}
