using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using AuthManager.Application.Authentication;

namespace AuthManager.Infrastructure.Identity.EmailVerification;

public sealed class GmailEmailVerificationDeliveryOptions
{
    public required string SenderAddress { get; init; }
    public required string ApprovedRecipientAddress { get; init; }

    // The composition root must admit the dedicated app-password file through the
    // existing protected-file gate. Neither a password nor a token is configuration.
    public required string AppPasswordFile { get; init; }

    public required Uri VerificationLandingUri { get; init; }
    public TimeSpan SendDeadline { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>The bounded M1 Gmail adapter, not a general mail relay.</summary>
public sealed class GmailEmailVerificationDelivery : IEmailVerificationDelivery
{
    private readonly GmailEmailVerificationDeliveryOptions _options;
    private readonly MailAddress _sender;
    private readonly MailAddress _recipient;

    public GmailEmailVerificationDelivery(GmailEmailVerificationDeliveryOptions options)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(options);
            _sender = ExactAddress(options.SenderAddress);
            _recipient = ExactAddress(options.ApprovedRecipientAddress);
            if (string.IsNullOrWhiteSpace(options.AppPasswordFile)
                || !Path.IsPathFullyQualified(options.AppPasswordFile)
                || !IsLandingUri(options.VerificationLandingUri)
                || options.SendDeadline < TimeSpan.FromSeconds(1)
                || options.SendDeadline > TimeSpan.FromMinutes(1))
                throw new ArgumentException();
            _options = options;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.InvalidConfiguration);
        }
    }

    public async Task DeliverAsync(
        string recipient,
        Uri verificationLink,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Validate all nonsecret input before opening the admitted credential file.
        if (!string.Equals(recipient, _recipient.Address, StringComparison.OrdinalIgnoreCase))
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.InvalidRecipient);
        if (!IsVerificationLink(verificationLink))
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.InvalidVerificationLink);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.SendDeadline);

        try
        {
            var appPassword = await ReadAdmittedAppPasswordAsync(deadline.Token);
            using var message = new MailMessage
            {
                From = _sender,
                Subject = "Verify your Zeka email address",
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = false,
                Body = "Open this link and confirm verification on the page:\n\n"
                    + verificationLink.AbsoluteUri
                    + "\n\nIf you did not request this, do not confirm it."
            };
            message.To.Add(_recipient);

            // EnableSsl requires STARTTLS; the framework performs normal certificate
            // validation. No trust override, ordinary account password, default
            // credentials, pickup-directory delivery, or alternate endpoint is used.
            using var smtp = new SmtpClient("smtp.gmail.com", 587)
            {
                EnableSsl = true,
                UseDefaultCredentials = false,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(_sender.Address, appPassword)
            };
            await smtp.SendMailAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.DeadlineExceeded);
        }
        catch (SmtpException exception)
        {
            throw new GmailEmailVerificationDeliveryException(exception);
        }
        catch (EmailVerificationDeliveryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException
            or AuthenticationException or InvalidOperationException)
        {
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.TransportUnavailable);
        }
    }

    private bool IsVerificationLink(Uri? link) =>
        link is { IsAbsoluteUri: true }
        && link.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(link.UserInfo)
        && string.IsNullOrEmpty(link.Query)
        && link.Fragment.Length > 1
        && link.AbsoluteUri.Length <= 4096
        && string.Equals(link.GetLeftPart(UriPartial.Path),
            _options.VerificationLandingUri.AbsoluteUri, StringComparison.Ordinal);

    private static bool IsLandingUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true }
        && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);

    private static MailAddress ExactAddress(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var parsed = new MailAddress(address);
        if (!string.Equals(address, parsed.Address, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException();
        return parsed;
    }

    private async Task<string> ReadAdmittedAppPasswordAsync(CancellationToken cancellationToken)
    {
        try
        {
            var attributes = File.GetAttributes(_options.AppPasswordFile);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException();
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(_options.AppPasswordFile);
                const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute;
                if ((mode & forbidden) != 0)
                    throw new IOException();
            }

            await using var file = new FileStream(_options.AppPasswordFile, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is < 16 or > 128)
                throw new IOException();
            using var reader = new StreamReader(file, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 128, leaveOpen: true);
            var password = (await reader.ReadToEndAsync(cancellationToken)).Trim();
            if (password.Length != 16 || password.Any(character =>
                    !char.IsAsciiLetterOrDigit(character)))
                throw new IOException();
            return password;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            throw new EmailVerificationDeliveryException(
                EmailVerificationDeliveryFailure.CredentialFileUnavailable);
        }
    }
}

/// <summary>Preserves a native numeric status without retaining SMTP diagnostics.</summary>
public sealed class GmailEmailVerificationDeliveryException : EmailVerificationDeliveryException
{
    public GmailEmailVerificationDeliveryException(SmtpException nativeFailure)
        : base(CategoryFor(nativeFailure), (int)nativeFailure.StatusCode)
    {
    }

    private static EmailVerificationDeliveryFailure CategoryFor(SmtpException nativeFailure)
    {
        ArgumentNullException.ThrowIfNull(nativeFailure);
        if (nativeFailure.InnerException is AuthenticationException)
            return EmailVerificationDeliveryFailure.TlsRejected;
        return (int)nativeFailure.StatusCode switch
        {
            534 or 535 or 538 => EmailVerificationDeliveryFailure.AuthenticationRejected,
            _ => EmailVerificationDeliveryFailure.SmtpRejected
        };
    }
}
