using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class ClientRuntimeConnectionConfigurationTests
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
            ["ConnectionStrings:ClientApiConnection"] = value
        });

        var error = Assert.Throws<InvalidOperationException>(() =>
            ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(new ServiceCollection(), configuration));
        Assert.Equal("ConnectionStrings:ClientApiConnection is required.", error.Message);
    }
}
