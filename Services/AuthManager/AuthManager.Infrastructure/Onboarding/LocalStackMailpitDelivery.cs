using System.Net.Mail;

namespace AuthManager.Infrastructure.Onboarding;

internal interface IVerificationMailDelivery
{
    Task SendAsync(string recipient, string token, CancellationToken cancellationToken);
}

internal sealed class LocalStackMailpitDelivery(RegistrationOptions options) : IVerificationMailDelivery
{
    public async Task SendAsync(string recipient, string token, CancellationToken cancellationToken)
    {
        if (!options.MailpitEnabled || options.VerificationPage is null)
            throw new InvalidOperationException("LOCAL_VERIFICATION_MAIL_NOT_ADMITTED");
        var link = new UriBuilder(options.VerificationPage) { Fragment = "token=" + token }.Uri.AbsoluteUri;
        using var message = new MailMessage(new MailAddress("verification@zeka-m1.test"), new MailAddress(recipient))
        {
            Subject = "Verify your Zeka email address",
            Body = $"Open this link to verify your email address: {link}",
            IsBodyHtml = false
        };
        // The LocalStack adapter has one fixed private sink and no SMTP relay credentials.
        using var smtp = new SmtpClient("mailpit", 1025) { EnableSsl = false, UseDefaultCredentials = false };
        await smtp.SendMailAsync(message, cancellationToken);
    }
}
