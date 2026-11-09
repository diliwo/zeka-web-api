using System.Net.Mail;
using System.Security.Authentication;
using AuthManager.Application.Authentication;
using AuthManager.Infrastructure.Identity.EmailVerification;
using FluentAssertions;

namespace Infrastructure.IntegrationTests;

/// <summary>Unit fixtures only: these cases never connect to SMTP or send a message.</summary>
public sealed class GmailEmailVerificationDeliveryTests
{
    private const string Recipient = "owner@example.org";

    [Theory]
    [InlineData("http://auth.example.test/verify")]
    [InlineData("https://auth.example.test/verify?value=unit-fixture")]
    [InlineData("https://auth.example.test/verify#unit-fixture")]
    [InlineData("https://user@auth.example.test/verify")]
    public void Unit_fixture_rejects_unsafe_landing_configuration_without_sending(string landing)
    {
        var action = () => new GmailEmailVerificationDelivery(
            Options(new Uri(landing)));

        action.Should().Throw<EmailVerificationDeliveryException>()
            .Which.Category.Should().Be(EmailVerificationDeliveryFailure.InvalidConfiguration);
    }

    [Fact]
    public void Unit_fixture_requires_absolute_file_reference_without_accepting_a_password_value()
    {
        var options = new GmailEmailVerificationDeliveryOptions
        {
            SenderAddress = Recipient,
            ApprovedRecipientAddress = Recipient,
            AppPasswordFile = "relative-unit-fixture",
            VerificationLandingUri = new Uri("https://auth.example.test/verify")
        };

        var action = () => new GmailEmailVerificationDelivery(options);

        action.Should().Throw<EmailVerificationDeliveryException>()
            .Which.Category.Should().Be(EmailVerificationDeliveryFailure.InvalidConfiguration);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void Unit_fixture_refuses_an_unbounded_delivery_deadline(int seconds)
    {
        var options = new GmailEmailVerificationDeliveryOptions
        {
            SenderAddress = Recipient,
            ApprovedRecipientAddress = Recipient,
            AppPasswordFile = MissingCredentialPath(),
            VerificationLandingUri = new Uri("https://auth.example.test/verify"),
            SendDeadline = TimeSpan.FromSeconds(seconds)
        };

        var action = () => new GmailEmailVerificationDelivery(options);

        action.Should().Throw<EmailVerificationDeliveryException>()
            .Which.Category.Should().Be(EmailVerificationDeliveryFailure.InvalidConfiguration);
    }

    [Fact]
    public async Task Unit_fixture_refuses_unapproved_recipient_before_credential_access()
    {
        var delivery = new GmailEmailVerificationDelivery(Options());

        var action = () => delivery.DeliverAsync(
            "other@example.org", ValidLink());

        var failure = (await action.Should().ThrowAsync<EmailVerificationDeliveryException>()).Which;
        failure.Category.Should().Be(EmailVerificationDeliveryFailure.InvalidRecipient);
        failure.InnerException.Should().BeNull();
    }

    [Theory]
    [InlineData("http://auth.example.test/verify#unit-fixture")]
    [InlineData("https://other.example.test/verify#unit-fixture")]
    [InlineData("https://auth.example.test/other#unit-fixture")]
    [InlineData("https://auth.example.test/verify?value=unit-fixture")]
    [InlineData("https://auth.example.test/verify")]
    public async Task Unit_fixture_refuses_unsafe_or_unbound_link_before_credential_access(string link)
    {
        var delivery = new GmailEmailVerificationDelivery(Options());

        var action = () => delivery.DeliverAsync(Recipient, new Uri(link));

        (await action.Should().ThrowAsync<EmailVerificationDeliveryException>())
            .Which.Category.Should().Be(EmailVerificationDeliveryFailure.InvalidVerificationLink);
    }

    [Fact]
    public async Task Unit_fixture_missing_credential_file_fails_closed_without_network_or_sensitive_error()
    {
        var options = Options();
        var delivery = new GmailEmailVerificationDelivery(options);
        var link = ValidLink();

        var action = () => delivery.DeliverAsync(Recipient, link);

        var failure = (await action.Should().ThrowAsync<EmailVerificationDeliveryException>()).Which;
        failure.Category.Should().Be(EmailVerificationDeliveryFailure.CredentialFileUnavailable);
        failure.Message.Should().Be("Email verification delivery failed.");
        failure.ToString().Should().NotContain(options.AppPasswordFile)
            .And.NotContain(link.AbsoluteUri).And.NotContain(Recipient);
        failure.InnerException.Should().BeNull();
        failure.SmtpStatusCode.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("unit-fixture-is-not-an-app-password")]
    [InlineData("unit fixture one")]
    public async Task Unit_fixture_malformed_file_fails_closed_without_network(string fixtureText)
    {
        var directory = Path.Combine(Path.GetTempPath(), "zeka-mail-unit-fixture",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "invalid-fixture.txt");
        try
        {
            // This is deliberately invalid, nonsecret fixture text, never a credential.
            await File.WriteAllTextAsync(file, fixtureText);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var options = new GmailEmailVerificationDeliveryOptions
            {
                SenderAddress = Recipient,
                ApprovedRecipientAddress = Recipient,
                AppPasswordFile = file,
                VerificationLandingUri = new Uri("https://auth.example.test/verify")
            };
            var delivery = new GmailEmailVerificationDelivery(options);

            var action = () => delivery.DeliverAsync(Recipient, ValidLink());

            var failure = (await action.Should().ThrowAsync<EmailVerificationDeliveryException>()).Which;
            failure.Category.Should().Be(EmailVerificationDeliveryFailure.CredentialFileUnavailable);
            failure.InnerException.Should().BeNull();
            failure.Message.Should().Be("Email verification delivery failed.");
            failure.ToString().Should().NotContain(file);
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }
    [Fact]
    public async Task Unit_fixture_cancellation_stops_before_credential_access_or_network()
    {
        var delivery = new GmailEmailVerificationDelivery(Options());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => delivery.DeliverAsync(Recipient, ValidLink(), cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Theory]
    [InlineData(535, EmailVerificationDeliveryFailure.AuthenticationRejected)]
    [InlineData(534, EmailVerificationDeliveryFailure.AuthenticationRejected)]
    [InlineData(550, EmailVerificationDeliveryFailure.SmtpRejected)]
    public void Unit_fixture_preserves_native_smtp_status_but_discards_message_and_inner_failure(
        int status,
        EmailVerificationDeliveryFailure expected)
    {
        var native = new SmtpException((SmtpStatusCode)status,
            "unit-fixture native detail must never escape");
        var safe = new GmailEmailVerificationDeliveryException(native);

        safe.Category.Should().Be(expected);
        safe.SmtpStatusCode.Should().Be(status);
        safe.Message.Should().Be("Email verification delivery failed.");
        safe.InnerException.Should().BeNull();
        safe.ToString().Should().NotContain("unit-fixture native detail");
    }

    [Fact]
    public void Unit_fixture_retains_tls_failure_category_without_native_exception_text()
    {
        var native = new SmtpException("unit-fixture SMTP detail",
            new AuthenticationException("unit-fixture certificate detail"));

        var safe = new GmailEmailVerificationDeliveryException(native);

        safe.Category.Should().Be(EmailVerificationDeliveryFailure.TlsRejected);
        safe.InnerException.Should().BeNull();
        safe.ToString().Should().NotContain("unit-fixture SMTP detail")
            .And.NotContain("unit-fixture certificate detail");
    }

    private static GmailEmailVerificationDeliveryOptions Options(Uri? landing = null) => new()
    {
        SenderAddress = Recipient,
        ApprovedRecipientAddress = Recipient,
        AppPasswordFile = MissingCredentialPath(),
        VerificationLandingUri = landing ?? new Uri("https://auth.example.test/verify")
    };

    private static string MissingCredentialPath() =>
        Path.Combine(Path.GetTempPath(), "zeka-mail-unit-fixture", Guid.NewGuid().ToString("N"));

    private static Uri ValidLink() =>
        new("https://auth.example.test/verify#unit-fixture-verification");
}
