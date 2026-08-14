using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Enums;

namespace AuthManager.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void Core_has_no_framework_or_outer_layer_dependencies()
    {
        var references = typeof(UserStatus).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain("AuthManager.Application", references);
        Assert.DoesNotContain("AuthManager.Infrastructure", references);
        Assert.DoesNotContain("AuthManager.API", references);
    }

    [Fact]
    public void Application_has_no_identity_or_outer_layer_dependencies()
    {
        var references = typeof(IAuthenticationService).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        Assert.DoesNotContain(references, name => name!.StartsWith("Microsoft.AspNetCore.Identity", StringComparison.Ordinal));
        Assert.DoesNotContain("AuthManager.Infrastructure", references);
        Assert.DoesNotContain("AuthManager.API", references);
    }
}
