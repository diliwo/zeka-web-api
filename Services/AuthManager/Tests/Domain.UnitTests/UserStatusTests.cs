using AuthManager.Core.Enums;
using FluentAssertions;

namespace Domain.UnitTests;

public sealed class UserStatusTests
{
    [Fact]
    public void User_status_contains_only_the_supported_lifecycle_states()
    {
        Enum.GetValues<UserStatus>()
            .Should()
            .Equal(
                UserStatus.PendingVerification,
                UserStatus.Active,
                UserStatus.Suspended,
                UserStatus.Deleted);
    }

    [Fact]
    public void Domain_has_no_framework_or_outer_layer_dependencies()
    {
        var references = typeof(UserStatus).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Where(name => name is not null)
            .ToArray();

        references.Should().NotContain(name => name!.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        references.Should().NotContain(name => name!.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        references.Should().NotContain("AuthManager.Application");
        references.Should().NotContain("AuthManager.Infrastructure");
        references.Should().NotContain("AuthManager.API");
    }
}
