using AutoFixture;
using AuthManager.Application;
using AuthManager.Application.Authentication.Models;
using AuthManager.Application.Common.Interfaces;
using AuthManager.Application.Common.Validators.Users;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Application.IntegrationTests;

public sealed class ApplicationDependencyInjectionTests
{
    [Fact]
    public async Task Application_services_resolve_and_validate_a_registration_request()
    {
        var fixture = new Fixture();
        var authenticationService = new Mock<IAuthenticationService>();
        var services = new ServiceCollection();
        services.AddScoped(_ => authenticationService.Object);
        services.Application();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var serviceManager = scope.ServiceProvider.GetRequiredService<IServiceManager>();
        var validator = scope.ServiceProvider.GetRequiredService<IValidator<RegisterUserRequest>>();
        var request = fixture.Build<RegisterUserRequest>()
            .With(value => value.Email, "owner@example.org")
            .With(value => value.UserName, "owner")
            .With(value => value.FirstName, "Ada")
            .With(value => value.LastName, "Lovelace")
            .With(value => value.Password, "Correct-Horse-7!")
            .Create();

        var validation = await validator.ValidateAsync(request);

        serviceManager.AuthenticationService.Should().BeSameAs(authenticationService.Object);
        validation.IsValid.Should().BeTrue();
    }
}
