using AuthManager.Application.Provisioning;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Zeka.AuthOwnerProvision;

// This privileged source is registered only by this private owner command.
// Public API composition must use its normal confirmed-subject authority source.
internal sealed class OwnerIdentitySource(
    AdmittedOwnerInput admitted, UserManager<User> users,
    AuthDbContext database, TimeProvider clock) : IProvisioningIdentitySource
{
    public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ProvisioningIdentity(
            admitted.Value.SubjectId, ProvisioningAuthorityKind.PrivilegedLocalOwner));
    }

    public async Task<Guid> EnsureConfirmedAsync(
        ProvisioningIdentity identity, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = admitted.Value;
        if (database.Database.CurrentTransaction is null
            || identity.SubjectId != input.SubjectId
            || identity.AuthorityKind != ProvisioningAuthorityKind.PrivilegedLocalOwner
            || users.NormalizeEmail("herve.mbilo@gmail.com") != OwnerProtocol.ApprovedEmail)
            throw new OwnerToolException("OWNER_IDENTITY_REJECTED");

        var existing = await users.FindByIdAsync(input.SubjectId.ToString("D"));
        var byEmail = await users.FindByEmailAsync("herve.mbilo@gmail.com");
        if (existing is not null || byEmail is not null)
        {
            if (existing is null || byEmail is null || existing.Id != byEmail.Id
                || existing.Id != input.SubjectId || existing.NormalizedEmail != OwnerProtocol.ApprovedEmail
                || !string.Equals(existing.Email, "herve.mbilo@gmail.com", StringComparison.OrdinalIgnoreCase)
                || existing.Status != UserStatus.Active || !existing.EmailConfirmed)
                throw new OwnerToolException("OWNER_EXISTING_IDENTITY_REJECTED");
            return existing.Id; // Never reset or replace an existing password.
        }

        var user = User.Create("herve.mbilo@gmail.com", "herve.mbilo@gmail.com",
            "Local", "Owner", clock.GetUtcNow());
        user.Id = input.SubjectId;
        // This is solely the admitted local-owner exception, never a request-body
        // emailVerified flag or a claim of mailbox verification.
        user.EmailConfirmed = true;
        user.ActivateConfirmedIdentity(clock.GetUtcNow());
        var created = await users.CreateAsync(user, input.OwnerPassword);
        if (!created.Succeeded) throw new OwnerToolException("OWNER_IDENTITY_CREATION_REJECTED");
        return user.Id;
    }
}
