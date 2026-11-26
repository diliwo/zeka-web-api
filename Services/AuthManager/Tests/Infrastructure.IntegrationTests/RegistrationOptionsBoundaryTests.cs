using System.Security.Cryptography;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Onboarding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace Infrastructure.IntegrationTests;

public sealed class RegistrationOptionsBoundaryTests
{
    [Fact]
    public void Disabled_registration_does_not_read_or_require_signing_material()
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:Enabled"] = null;
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "false";
        input.Values["OnboardingRegistration:VerificationKeyFiles:active"] = "not-an-absolute-file";
        using var options = input.Read();
        Assert.False(options.Enabled);
        Assert.False(options.MailpitEnabled);
        Assert.Null(options.VerificationPage);
        Assert.Empty(options.Keys);
    }

    [Theory]
    [InlineData(null, "LocalStack")]
    [InlineData("false", "LocalStack")]
    [InlineData(null, "Production")]
    [InlineData("false", "Production")]
    public void Enabled_registration_without_delivery_is_refused_before_service_registration(string? mailpit, string environment)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = mailpit;
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddVerificationRegistration(
            new ConfigurationBuilder().AddInMemoryCollection(input.Values).Build(),
            Mock.Of<IHostEnvironment>(value => value.EnvironmentName == environment)));

        Assert.Equal("ONBOARDING_REGISTRATION_CONFIGURATION_REJECTED", error.Message);
        Assert.Empty(services); // No endpoint service, readiness or worker can be installed.
    }

    [Fact]
    public void Supported_local_registration_installs_the_real_delivery_and_readiness_worker()
    {
        using var input = new RegistrationOptionInputs();
        var services = new ServiceCollection();
        services.AddVerificationRegistration(new ConfigurationBuilder().AddInMemoryCollection(input.Values).Build(),
            Mock.Of<IHostEnvironment>(value => value.EnvironmentName == "LocalStack"));
        Assert.Single(services, value => value.ServiceType == typeof(RegistrationReadiness));
        Assert.Single(services, value => value.ServiceType == typeof(VerificationMailDispatcher));
        Assert.Single(services, value => value.ServiceType == typeof(IHostedService)
            && value.ImplementationType == typeof(VerificationMailWorker));
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<LocalStackMailpitDelivery>(scope.ServiceProvider.GetRequiredService<IVerificationMailDelivery>());
        var options = provider.GetRequiredService<RegistrationOptions>();
        Assert.True(options.Enabled);
        Assert.True(options.MailpitEnabled);
        Assert.Equal("https://app.example.invalid:19444/verify/email", options.VerificationPage!.AbsoluteUri);
    }

    [Fact]
    public void Disabled_registration_installs_no_readiness_or_delivery_worker()
    {
        var services = new ServiceCollection();
        services.AddVerificationRegistration(new ConfigurationBuilder().Build(),
            Mock.Of<IHostEnvironment>(value => value.EnvironmentName == "Production"));
        Assert.DoesNotContain(services, value => value.ServiceType == typeof(RegistrationReadiness)
            || value.ServiceType == typeof(VerificationMailDispatcher)
            || value.ServiceType == typeof(IVerificationMailDelivery)
            || value.ServiceType == typeof(IHostedService));
        using var provider = services.BuildServiceProvider();
        Assert.False(provider.GetRequiredService<RegistrationOptions>().Enabled);
    }

    [Theory]
    [InlineData("Enabled")]
    [InlineData("VerificationMailpitEnabled")]
    public void Non_boolean_feature_flags_are_refused(string name)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:" + name] = "sometimes";
        AssertRejected(input);
    }

    [Theory]
    [InlineData(false, "LocalStack")]
    [InlineData(true, "Production")]
    [InlineData(true, "Development")]
    public void Mailpit_requires_both_registration_and_the_local_environment(bool enabled, string environment)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:Enabled"] = enabled.ToString();
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        AssertRejected(input, environment);
    }

    [Theory]
    [InlineData("Deployment", null)]
    [InlineData("Deployment", "")]
    [InlineData("Deployment", "tenant/name")]
    [InlineData("Deployment", "unicode-deployment")]
    [InlineData("ActiveKeyId", null)]
    [InlineData("ActiveKeyId", "")]
    [InlineData("ActiveKeyId", "key.name")]
    public void Missing_or_unsafe_authority_identifiers_are_refused(string name, string? value)
    {
        using var input = new RegistrationOptionInputs();
        // Keep discovery metadata ASCII; the admitted negative still receives the same Unicode input.
        input.Values["OnboardingRegistration:" + name] = value == "unicode-deployment" ? "d\u00e9ployment" : value;
        AssertRejected(input);
    }

    [Theory]
    [InlineData("Deployment", 121)]
    [InlineData("ActiveKeyId", 33)]
    public void Authority_identifier_length_limits_are_enforced(string name, int length)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:" + name] = new string('a', length);
        AssertRejected(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("relative")]
    [InlineData("http://app.example.invalid")]
    [InlineData("https://user@app.example.invalid")]
    [InlineData("https://app.example.invalid/")]
    [InlineData("https://app.example.invalid/path")]
    [InlineData("https://app.example.invalid?query=1")]
    [InlineData("https://app.example.invalid#fragment")]
    public void Verification_page_requires_an_exact_https_origin(string? origin)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        input.Values["BrowserSession:Origin"] = origin;
        AssertRejected(input);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("verify")]
    [InlineData("//verify")]
    [InlineData("/verify/")]
    [InlineData("/verify//email")]
    [InlineData("/../verify")]
    [InlineData("/verify?email=1")]
    [InlineData("/verify#email")]
    [InlineData("/%2fverify")]
    public void Verification_path_rejects_ambiguous_or_non_identifier_segments(string? path)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        input.Values["OnboardingRegistration:VerificationPagePath"] = path;
        AssertRejected(input);
    }

    [Theory]
    [InlineData(65, 1)]
    [InlineData(64, 2)]
    public void Verification_path_and_segment_lengths_are_bounded(int segmentLength, int segments)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        input.Values["OnboardingRegistration:VerificationPagePath"] = "/" + string.Join('/', Enumerable.Repeat(new string('a', segmentLength), segments));
        AssertRejected(input);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void Key_files_require_exactly_32_bytes_and_publish_only_a_safe_error(int size)
    {
        using var input = new RegistrationOptionInputs();
        File.WriteAllBytes(input.KeyFile, RandomNumberGenerator.GetBytes(size));
        AssertRejected(input);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.bin")]
    public void Key_files_require_a_nonempty_absolute_path(string file)
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationKeyFiles:active"] = file;
        AssertRejected(input);
    }

    [Fact]
    public void Missing_files_unknown_active_keys_and_unsafe_key_names_fail_closed()
    {
        using var input = new RegistrationOptionInputs();
        input.Values["OnboardingRegistration:VerificationKeyFiles:active"] = Path.Combine(input.Directory, "absent.bin");
        AssertRejected(input);
        input.Values["OnboardingRegistration:VerificationKeyFiles:active"] = input.KeyFile;
        input.Values["OnboardingRegistration:ActiveKeyId"] = "unknown";
        AssertRejected(input);
        input.Values["OnboardingRegistration:ActiveKeyId"] = "active";
        input.Values["OnboardingRegistration:VerificationKeyFiles:unsafe.name"] = input.KeyFile;
        AssertRejected(input);
    }

    [Fact]
    public void Eight_retained_keys_are_supported_but_a_ninth_is_refused()
    {
        using var input = new RegistrationOptionInputs();
        for (var index = 1; index < 8; index++)
            input.Values["OnboardingRegistration:VerificationKeyFiles:key_" + index] = input.KeyFile;
        using (var options = input.Read()) Assert.Equal(8, options.Keys.Count);
        input.Values["OnboardingRegistration:VerificationKeyFiles:ninth"] = input.KeyFile;
        AssertRejected(input);
    }

    [Fact]
    public void Accepted_local_page_and_maximum_identifiers_bind_real_key_bytes_and_zero_on_disposal()
    {
        using var input = new RegistrationOptionInputs();
        var keyId = new string('a', 32);
        input.Values.Remove("OnboardingRegistration:VerificationKeyFiles:active");
        input.Values["OnboardingRegistration:VerificationKeyFiles:" + keyId] = input.KeyFile;
        input.Values["OnboardingRegistration:ActiveKeyId"] = keyId;
        input.Values["OnboardingRegistration:Deployment"] = new string('d', 120);
        input.Values["OnboardingRegistration:VerificationMailpitEnabled"] = "true";
        using var options = input.Read();
        Assert.True(options.Enabled);
        Assert.True(options.MailpitEnabled);
        Assert.Equal("https://app.example.invalid:19444/verify/email", options.VerificationPage!.AbsoluteUri);
        Assert.Equal(keyId, options.ActiveKeyId);
        var loaded = options.Keys[keyId];
        Assert.Equal(File.ReadAllBytes(input.KeyFile), loaded);
        options.Dispose();
        Assert.All(loaded, value => Assert.Equal(0, value));
    }

    private static void AssertRejected(RegistrationOptionInputs input, string environment = "LocalStack")
        => Assert.Equal("ONBOARDING_REGISTRATION_CONFIGURATION_REJECTED",
            Assert.Throws<InvalidOperationException>(() => input.Read(environment)).Message);
}

internal sealed class RegistrationOptionInputs : IDisposable
{
    public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("zeka-registration-options-").FullName;
    public string KeyFile { get; }
    public Dictionary<string, string?> Values { get; } = new()
    {
        ["OnboardingRegistration:Enabled"] = "true",
        ["OnboardingRegistration:Deployment"] = "unit-fixture",
        ["OnboardingRegistration:ActiveKeyId"] = "active",
        ["OnboardingRegistration:VerificationMailpitEnabled"] = "true",
        ["OnboardingRegistration:VerificationPagePath"] = "/verify/email",
        ["BrowserSession:Origin"] = "https://app.example.invalid:19444"
    };

    public RegistrationOptionInputs()
    {
        KeyFile = Path.Combine(Directory, "unit-key.bin");
        File.WriteAllBytes(KeyFile, RandomNumberGenerator.GetBytes(32));
        Values["OnboardingRegistration:VerificationKeyFiles:active"] = KeyFile;
    }

    public RegistrationOptions Read(string environment = "LocalStack")
        => RegistrationOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(Values).Build(),
            Mock.Of<IHostEnvironment>(value => value.EnvironmentName == environment));

    public void Dispose()
    {
        File.Delete(KeyFile);
        System.IO.Directory.Delete(Directory);
    }
}
