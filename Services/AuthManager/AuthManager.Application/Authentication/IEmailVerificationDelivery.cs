namespace AuthManager.Application.Authentication;

/// <summary>Delivers a verification link; never treats delivery as verification proof.</summary>
public interface IEmailVerificationDelivery
{
    Task DeliverAsync(
        string recipient,
        Uri verificationLink,
        CancellationToken cancellationToken = default);
}

public enum EmailVerificationDeliveryFailure
{
    InvalidConfiguration,
    InvalidRecipient,
    InvalidVerificationLink,
    CredentialFileUnavailable,
    AuthenticationRejected,
    TlsRejected,
    SmtpRejected,
    TransportUnavailable,
    DeadlineExceeded
}

/// <summary>Allowlisted diagnostics only. Never retain a native exception or message.</summary>
public class EmailVerificationDeliveryException : Exception
{
    public EmailVerificationDeliveryException(
        EmailVerificationDeliveryFailure category,
        int? smtpStatusCode = null)
        : base("Email verification delivery failed.")
    {
        Category = category;
        SmtpStatusCode = smtpStatusCode;
    }

    public EmailVerificationDeliveryFailure Category { get; }

    public int? SmtpStatusCode { get; }
}
