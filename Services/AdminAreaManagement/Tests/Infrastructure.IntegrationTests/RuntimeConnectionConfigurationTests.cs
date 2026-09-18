using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class AdminAreaRuntimeConnectionConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"zeka-admin-config-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Both_composition_roots_require_a_nonblank_runtime_connection(string? value)
    {
        var main = Configuration(value);
        var alternate = Configuration(value);

        var mainError = Assert.Throws<InvalidOperationException>(() =>
            AdminAreaManagement.Infrastructure.DependencyInjection.AddInfrastructure(new ServiceCollection(), main));
        var alternateError = Assert.Throws<InvalidOperationException>(() =>
            AdminAreaManagement.Infrastructure.Persistence.DependencyInjection.AddInfrastructure(
                new ServiceCollection(), alternate));

        Assert.Equal("ConnectionStrings:ClientApiConnection is required.", mainError.Message);
        Assert.Equal(mainError.Message, alternateError.Message);
    }

    [Fact]
    public void Alternate_composition_root_registers_the_required_attempt_observer()
    {
        var services = new ServiceCollection();
        AdminAreaManagement.Infrastructure.Persistence.DependencyInjection.AddInfrastructure(
            services, Configuration("Host=localhost;Database=test;Username=test;Password=test"));

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<Zeka.PersistenceSecurity.ITenantAttemptOrderObserver>());
        Assert.NotNull(provider.GetRequiredService<AdminAreaManagement.Core.Interfaces.IFileService>());
    }

    private ConfigurationManager Configuration(string? value)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileServerPath"] = root,
            ["ConnectionStrings:ClientApiConnection"] = value
        });
        return configuration;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
