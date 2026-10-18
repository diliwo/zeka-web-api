using System.Security.Cryptography;
using System.Text;
using AuthManager.Application.Authentication;
using AuthManager.Application.Common.Auditing;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AuthManager.Infrastructure.Identity;

public sealed class BrowserSessions(AuthDbContext database, UserManager<User> users,
    IAuditWriter audit, ISessionAccessTokenIssuer issuer, BrowserSessionPolicy policy, TimeProvider clock) : IBrowserSessions
{
    private static readonly PasswordHasher<User> DummyHasher = new();
    private static readonly User DummyUser = User.Create("nobody@example.invalid", "nobody", "Synthetic", "Dummy", DateTimeOffset.UnixEpoch);
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    public Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken)
        => Once(async () =>
        {
            if (!policy.IsValid) return Unavailable();
            // Resolve the keyed hash before accepting a password. Missing key material cannot create a session.
            var secret = NewSecret();
            var hash = await HashAsync(secret, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (await FindLockedAsync(previousCookie, cancellationToken) is { } previous)
                await RevokeAsync(previous.Family, "replaced", cancellationToken);
            var user = await users.FindByEmailAsync(email);
            if (user is null)
            {
                DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
                await EventAsync(Guid.Empty, Guid.Empty, "sign_in_denied", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            user = await LockUserAsync(user.Id, cancellationToken);
            var validPassword = user is not null && await users.CheckPasswordAsync(user, password);
            if (user is null || !validPassword || !Eligible(user, clock.GetUtcNow()))
            {
                if (user is not null && !validPassword && !await users.IsLockedOutAsync(user))
                {
                    var failure = await users.AccessFailedAsync(user);
                    if (!failure.Succeeded) throw new InvalidOperationException("Identity write unavailable.");
                }
                await EventAsync(Guid.Empty, Guid.Empty, "sign_in_denied", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded)
                throw new InvalidOperationException("Identity write unavailable.");
            var now = clock.GetUtcNow();
            var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = user.Id,
                SecurityStamp = user.SecurityStamp ?? "", CreatedAtUtc = now,
                IdleExpiresAtUtc = now + policy.IdleLifetime, AbsoluteExpiresAtUtc = now + policy.AbsoluteLifetime };
            if (string.IsNullOrEmpty(family.SecurityStamp)) throw new InvalidOperationException("Identity unavailable.");
            database.BrowserSessionFamilies.Add(family);
            database.BrowserRefreshCredentials.Add(new() { Hash = hash, FamilyId = family.Id, ExpiresAtUtc = Deadline(family) });
            var token = await issuer.IssueSessionAsync(user.Id, family.Id, policy.AccessLifetime, cancellationToken);
            await EventAsync(user.Id, family.Id, "signed_in", cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Success(family, token, secret, now);
        }, cancellationToken);

    public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken)
        => Once(async () =>
        {
            if (!policy.IsValid) return Unavailable();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var match = await FindLockedAsync(cookie, cancellationToken);
            if (match is null) return Denied();
            var (family, credential) = match.Value;
            var now = clock.GetUtcNow();
            if (credential.ConsumedAtUtc is not null)
            {
                await RevokeAsync(family, "refresh_reuse", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            if (!await ActiveAsync(family, now, cancellationToken) || credential.ExpiresAtUtc <= now)
            {
                await RevokeAsync(family, "restore_denied", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            var secret = NewSecret();
            var hash = await HashAsync(secret, cancellationToken);
            credential.ConsumedAtUtc = now;
            family.IdleExpiresAtUtc = Min(now + policy.IdleLifetime, family.AbsoluteExpiresAtUtc);
            database.BrowserRefreshCredentials.Add(new() { Hash = hash, FamilyId = family.Id, ExpiresAtUtc = Deadline(family) });
            var token = await issuer.IssueSessionAsync(family.UserId, family.Id, policy.AccessLifetime, cancellationToken);
            await EventAsync(family.UserId, family.Id, "rotated", cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Success(family, token, secret, now);
        }, cancellationToken);

    public async Task<BrowserSessionOutcome> LogoutAsync(string? cookie, CancellationToken cancellationToken)
        => (await Once(async () =>
        {
            if (!policy.IsValid) return Unavailable();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var match = await FindLockedAsync(cookie, cancellationToken);
            var now = clock.GetUtcNow();
            // Expired cookies cannot authenticate a tombstone confirmation. Keep hash history for reuse detection.
            if (match is null || match.Value.Credential.ExpiresAtUtc <= now) return Denied();
            await RevokeAsync(match.Value.Family, "logout", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new() { Outcome = BrowserSessionOutcome.Success };
        }, cancellationToken)).Outcome;

    public async Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
        => (await Once(async () =>
        {
            if (subjectId == Guid.Empty || sessionId == Guid.Empty) return Denied();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            var family = await LockFamilyAsync(sessionId, cancellationToken);
            if (family is null || family.UserId != subjectId) return Denied();
            var active = await ActiveAsync(family, clock.GetUtcNow(), cancellationToken);
            if (!active) await RevokeAsync(family, "session_invalidated", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new() { Outcome = active ? BrowserSessionOutcome.Success : BrowserSessionOutcome.Unauthenticated };
        }, cancellationToken)).Outcome;

    private async Task<bool> ActiveAsync(BrowserSessionFamily family, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (family.RevokedAtUtc is not null || Deadline(family) <= now) return false;
        var user = await LockUserAsync(family.UserId, cancellationToken);
        if (user is not null && Eligible(user, now) && !string.IsNullOrEmpty(user.SecurityStamp)
            && string.Equals(user.SecurityStamp, family.SecurityStamp, StringComparison.Ordinal)) return true;
        // Password resets change Identity's stamp. Suspension or a changed stamp invalidates all observed families.
        await database.BrowserSessionFamilies.Where(x => x.UserId == family.UserId && x.RevokedAtUtc == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, (DateTimeOffset?)now), cancellationToken);
        family.RevokedAtUtc = now;
        await EventAsync(family.UserId, family.Id, "identity_invalidated", cancellationToken);
        return false;
    }

    private async Task<(BrowserSessionFamily Family, BrowserRefreshCredential Credential)?> FindLockedAsync(string? cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length != 43) return null;
        var hash = await HashAsync(cookie, cancellationToken);
        var familyId = await database.BrowserRefreshCredentials.AsNoTracking()
            .Where(x => x.Hash == hash).Select(x => (Guid?)x.FamilyId).SingleOrDefaultAsync(cancellationToken);
        if (familyId is null) return null;
        var family = await LockFamilyAsync(familyId.Value, cancellationToken);
        var credential = await database.BrowserRefreshCredentials.SingleAsync(x => x.Hash == hash, cancellationToken);
        await database.Entry(credential).ReloadAsync(cancellationToken);
        return family is null ? null : (family, credential);
    }

    private async Task<BrowserSessionFamily?> LockFamilyAsync(Guid id, CancellationToken cancellationToken)
    {
        var family = await database.BrowserSessionFamilies.FromSqlInterpolated(
            $"SELECT * FROM \"BrowserSessionFamilies\" WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (family is not null) await database.Entry(family).ReloadAsync(cancellationToken);
        return family;
    }
    private async Task<User?> LockUserAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await database.Users.FromSqlInterpolated(
            $"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (user is not null) await database.Entry(user).ReloadAsync(cancellationToken);
        return user;
    }
    private async Task RevokeAsync(BrowserSessionFamily family, string outcome, CancellationToken cancellationToken)
    {
        if (family.RevokedAtUtc is not null) return;
        family.RevokedAtUtc = clock.GetUtcNow();
        await EventAsync(family.UserId, family.Id, outcome, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }
    private Task<Guid> EventAsync(Guid subject, Guid family, string outcome, CancellationToken cancellationToken)
        => audit.WriteAsync(new(subject, null, "BrowserSession", "SessionFamily", family.ToString("D"),
            outcome, Guid.NewGuid().ToString("N")), cancellationToken);

    private async Task<string> HashAsync(string secret, CancellationToken cancellationToken)
    {
        var key = await File.ReadAllBytesAsync(policy.HashKeyReference, cancellationToken);
        try
        {
            if (key.Length < 32) throw new InvalidOperationException("Session authority unavailable.");
            return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(secret)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private async Task<BrowserSessionResult> Once(Func<Task<BrowserSessionResult>> operation, CancellationToken cancellationToken)
    {
        var attempted = false;
        try
        {
            // The configured provider retries reads elsewhere. NEVER replay a session mutation after uncertain commit.
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (attempted) throw new InvalidOperationException("Session operation cannot be replayed.");
                attempted = true;
                return await operation();
            });
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
    }
    private static bool Eligible(User user, DateTimeOffset now) => user.Status == UserStatus.Active
        && user.EmailConfirmed && (!user.LockoutEnabled || user.LockoutEnd is null || user.LockoutEnd <= now);
    private static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static DateTimeOffset Deadline(BrowserSessionFamily family) => Min(family.IdleExpiresAtUtc, family.AbsoluteExpiresAtUtc);
    private BrowserSessionResult Success(BrowserSessionFamily family, string token, string secret, DateTimeOffset now) => new()
    { Outcome = BrowserSessionOutcome.Success, SubjectId = family.UserId, AccessToken = token, RefreshSecret = secret,
        ExpiresAtUtc = now + policy.AccessLifetime, CookieExpiresAtUtc = Deadline(family) };
    private static BrowserSessionResult Denied() => new() { Outcome = BrowserSessionOutcome.Unauthenticated };
    private static BrowserSessionResult Unavailable() => new() { Outcome = BrowserSessionOutcome.Unavailable };
}
