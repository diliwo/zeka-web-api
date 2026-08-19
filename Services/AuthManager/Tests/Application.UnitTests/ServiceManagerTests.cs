using AuthManager.Application.Common.Interfaces;
using AuthManager.Application.Common.Services;
using FluentAssertions;
using Moq;

namespace Application.UnitTests;

public sealed class ServiceManagerTests
{
    [Fact]
    public void Authentication_service_is_exposed_through_the_application_facade()
    {
        var authenticationService = new Mock<IAuthenticationService>();

        var serviceManager = new ServiceManager(authenticationService.Object);

        serviceManager.AuthenticationService.Should().BeSameAs(authenticationService.Object);
    }

    [Fact]
    public void Application_has_no_identity_or_outer_layer_dependencies()
    {
        var references = typeof(IAuthenticationService).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().NotContain(name => name!.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal));
        references.Should().NotContain("AuthManager.Infrastructure");
        references.Should().NotContain("AuthManager.API");
    }
}
