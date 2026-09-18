using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class AuthRuntimeConnectionConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Runtime_connection_must_be_nonblank(string? value)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = value
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthManager.Infrastructure.DependencyInjection.Infrastructure(new ServiceCollection(), configuration));
        Assert.Equal("ConnectionStrings:Default is required.", error.Message);
    }
}
