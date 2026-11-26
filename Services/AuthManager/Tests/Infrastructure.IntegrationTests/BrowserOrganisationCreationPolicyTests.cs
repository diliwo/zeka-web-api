using AuthManager.Infrastructure.Provisioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.IntegrationTests;

public sealed class BrowserOrganisationCreationPolicyTests
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Test")]
    public void Explicit_enablement_fails_closed_outside_LocalStack(string environment)
        => Assert.Throws<InvalidOperationException>(() => Read(environment, "true", null));

    [Fact]
    public void Local_only_feature_is_disabled_by_default_and_ownership_cap_defaults_to_one()
    {
        var disabled = Read("LocalStack", null, null);
        Assert.False(disabled.Enabled);
        Assert.Equal(1, disabled.MaxOwnedOrganisations);
        var enabled = Read("LocalStack", "true", null);
        Assert.True(enabled.Enabled);
        Assert.Equal(1, enabled.MaxOwnedOrganisations);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("101")]
    [InlineData("invalid")]
    public void Malformed_or_unbounded_limits_fail_closed(string maximum)
        => Assert.Throws<InvalidOperationException>(() => Read("LocalStack", "true", maximum));

    [Fact]
    public void Malformed_enablement_does_not_silently_enable_or_disable_the_policy()
        => Assert.Throws<InvalidOperationException>(() => Read("LocalStack", "invalid", null));

    private static BrowserOrganisationCreationPolicy Read(string environment, string? enabled, string? maximum)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BrowserOrganisationCreationPolicy.SectionName + ":Enabled"] = enabled,
            [BrowserOrganisationCreationPolicy.SectionName + ":MaxOwnedOrganisations"] = maximum
        }).Build();
        return BrowserOrganisationCreationPolicy.Read(configuration, new EnvironmentStub { EnvironmentName = environment });
    }

    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "";
        public string ApplicationName { get; set; } = "";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}