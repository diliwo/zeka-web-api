using AuthManager.Core.Enums;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Infrastructure.Identity.Models;

public sealed class User : IdentityUser<Guid>
{
    private User()
    {
    }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? UpdatedAtUtc { get; private set; }

    public void ActivateAfterEmailVerification(DateTimeOffset activatedAtUtc)
    {
        if (Status != UserStatus.PendingVerification || !EmailConfirmed || activatedAtUtc < CreatedAtUtc)
            throw new InvalidOperationException("USER_VERIFIED_ACTIVATION_REJECTED");
        Status = UserStatus.Active;
        UpdatedAtUtc = activatedAtUtc;
    }

    public static User Create(
        string email,
        string userName,
        string firstName,
        string lastName,
        DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(userName);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstName);
        ArgumentException.ThrowIfNullOrWhiteSpace(lastName);

        return new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = userName,
            FirstName = firstName,
            LastName = lastName,
            Status = UserStatus.PendingVerification,
            CreatedAtUtc = createdAtUtc,
            LockoutEnabled = true
        };
    }
}
