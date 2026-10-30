using AuthManager.Application.Authentication;
using AuthManager.Infrastructure.Identity.EmailVerification;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests;

public sealed class EmailVerificationCompositionTests
{
    [Fact]
    public void Explicit_owner_five_minute_policy_registers_only_the_exchange()
    {
        var configuration = Configuration("00:05:00");
        var services = new ServiceCollection();
        services.AddEmailVerificationExchange(configuration);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<EmailVerificationExchangeOptions>>()
            .Value.RestrictedGrantLifetime.Should().Be(TimeSpan.FromMinutes(5));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IEmailVerificationExchange)
            && descriptor.ImplementationType == typeof(EmailVerificationExchange)
            && descriptor.Lifetime == ServiceLifetime.Scoped);
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IEmailVerificationDelivery)
            || descriptor.ServiceType == typeof(IBrowserSessions));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("-00:05:00")]
    [InlineData("00:10:00")]
    [InlineData("1.00:00:00")]
    [InlineData("NOT_A_DURATION")]
    public void Missing_or_unapproved_policy_fails_before_any_exchange_or_credential_read(string? lifetime)
    {
        var services = new ServiceCollection();
        var action = () => services.AddEmailVerificationExchange(Configuration(lifetime));
        var failure = action.Should().Throw<InvalidOperationException>().Which;
        failure.Message.Should().Be("AUTH_ONBOARDING_CONFIGURATION_REJECTED");
        failure.InnerException.Should().BeNull();
        services.Should().BeEmpty();
    }

    [Fact]
    public void Unexpected_setting_is_refused_without_retaining_its_value()
    {
        const string marker = "NON_SENSITIVE_CONFIGURATION_MARKER";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AuthOnboarding:RestrictedGrantLifetime"] = "00:05:00",
                ["AuthOnboarding:Unexpected"] = marker
            }).Build();
        var services = new ServiceCollection();
        Action action = () => services.AddEmailVerificationExchange(configuration);
        var failure = action.Should().Throw<InvalidOperationException>().Which;
        failure.ToString().Should().NotContain(marker);
        services.Should().BeEmpty();
    }

    private static IConfiguration Configuration(string? lifetime) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AuthOnboarding:RestrictedGrantLifetime"] = lifetime
            }).Build();
}