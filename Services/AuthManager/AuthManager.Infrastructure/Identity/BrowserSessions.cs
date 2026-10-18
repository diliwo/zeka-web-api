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
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AuthManager.Infrastructure.Identity;

public sealed class BrowserSessions(AuthDbContext database, UserManager<User> users,
    IAuditWriter audit, ISessionAccessTokenIssuer issuer, BrowserSessionPolicy policy, TimeProvider clock) : IBrowserSessions
{
    private static readonly PasswordHasher<User> DummyHasher = new();
    private static readonly User DummyUser = User.Create("nobody@example.invalid", "nobody", "Synthetic", "Dummy", DateTimeOffset.UnixEpoch);
    private static readonly string DummyHash = DummyHasher.HashPassword(DummyUser, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    private static readonly Meter Metrics = new("Zeka.Authentication.BrowserSessions");
    private static readonly Counter<long> Failures = Metrics.CreateCounter<long>("browser_session.unavailable");
    private sealed class SessionFailure(string category) : Exception
    { public string Category { get; } = category; }
    private sealed record CredentialHint(Guid FamilyId, Guid UserId, string Hash);

    public Task<BrowserSessionResult> SignInAsync(string email, string password, string? previousCookie, CancellationToken cancellationToken)
        => Once("sign_in", async () =>
        {
            if (!policy.IsValid) throw new SessionFailure("policy_unavailable");
            // Resolve the keyed hash before accepting a password. Missing key material cannot create a session.
            var secret = NewSecret();
            var hash = await HashAsync(secret, cancellationToken);
            var user = await users.FindByEmailAsync(email);
            var previousHint = await FindHintAsync(previousCookie, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            // Every operation locks users before families. Cross-user replacement uses the same stable order.
            foreach (var id in new Guid?[] { user?.Id, previousHint?.UserId }.OfType<Guid>().Distinct().Order())
                await LockUserAsync(id, cancellationToken);
            if (user is null)
            {
                DummyHasher.VerifyHashedPassword(DummyUser, DummyHash, password);
                await EventAsync(Guid.Empty, Guid.Empty, "sign_in_denied", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            user = await LockUserAsync(user.Id, cancellationToken);
            var validPassword = user is not null && user.NormalizedEmail == users.NormalizeEmail(email)
                && await users.CheckPasswordAsync(user, password);
            if (user is null || !validPassword || !Eligible(user, clock.GetUtcNow()))
            {
                if (user is not null && !validPassword && !await users.IsLockedOutAsync(user))
                {
                    var failure = await users.AccessFailedAsync(user);
                    if (!failure.Succeeded) throw new SessionFailure("identity_write_unavailable");
                }
                await EventAsync(Guid.Empty, Guid.Empty, "sign_in_denied", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return Denied();
            }
            if (!(await users.ResetAccessFailedCountAsync(user)).Succeeded)
                throw new SessionFailure("identity_write_unavailable");
            if (await FindLockedAsync(previousHint, cancellationToken) is { } previous)
                await RevokeAsync(previous.Family, "replaced", cancellationToken);
            var now = clock.GetUtcNow();
            var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = user.Id,
                SecurityStamp = user.SecurityStamp ?? "", CreatedAtUtc = now,
                IdleExpiresAtUtc = now + policy.IdleLifetime, AbsoluteExpiresAtUtc = now + policy.AbsoluteLifetime };
            if (string.IsNullOrEmpty(family.SecurityStamp)) throw new SessionFailure("identity_unavailable");
            database.BrowserSessionFamilies.Add(family);
            database.BrowserRefreshCredentials.Add(new() { Hash = hash, FamilyId = family.Id, ExpiresAtUtc = Deadline(family) });
            var token = await issuer.IssueSessionAsync(user.Id, family.Id, policy.AccessLifetime, cancellationToken);
            await EventAsync(user.Id, family.Id, "signed_in", cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Success(family, token, secret, now);
        }, cancellationToken);

    public Task<BrowserSessionResult> RestoreAsync(string? cookie, CancellationToken cancellationToken)
        => Once("restore", async () =>
        {
            if (!policy.IsValid) throw new SessionFailure("policy_unavailable");
            var hint = await FindHintAsync(cookie, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (hint is null || await LockUserAsync(hint.UserId, cancellationToken) is null) return Denied();
            var match = await FindLockedAsync(hint, cancellationToken);
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
        => (await Once("logout", async () =>
        {
            if (!policy.IsValid) throw new SessionFailure("policy_unavailable");
            var hint = await FindHintAsync(cookie, cancellationToken);
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (hint is null || await LockUserAsync(hint.UserId, cancellationToken) is null) return Denied();
            var match = await FindLockedAsync(hint, cancellationToken);
            var now = clock.GetUtcNow();
            // Expired cookies cannot authenticate a tombstone confirmation. Keep hash history for reuse detection.
            if (match is null || match.Value.Credential.ExpiresAtUtc <= now) return Denied();
            await RevokeAsync(match.Value.Family, "logout", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new() { Outcome = BrowserSessionOutcome.Success };
        }, cancellationToken)).Outcome;

    public async Task<BrowserSessionOutcome> CheckActiveAsync(Guid subjectId, Guid sessionId, CancellationToken cancellationToken)
        => (await Once("check_active", async () =>
        {
            if (subjectId == Guid.Empty || sessionId == Guid.Empty) return Denied();
            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            if (await LockUserAsync(subjectId, cancellationToken) is null) return Denied();
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
        // The held user row serializes invalidation with issuance. A stale stamp cannot revoke a newer stamp.
        var affected = database.BrowserSessionFamilies.Where(x => x.UserId == family.UserId && x.RevokedAtUtc == null);
        if (user is not null && Eligible(user, now) && !string.IsNullOrEmpty(user.SecurityStamp))
            affected = affected.Where(x => x.SecurityStamp == family.SecurityStamp);
        await affected
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAtUtc, (DateTimeOffset?)now), cancellationToken);
        family.RevokedAtUtc = now;
        await EventAsync(family.UserId, family.Id, "identity_invalidated", cancellationToken);
        return false;
    }

    private async Task<CredentialHint?> FindHintAsync(string? cookie, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(cookie) || cookie.Length != 43) return null;
        var hash = await HashAsync(cookie, cancellationToken);
        return await (from credential in database.BrowserRefreshCredentials.AsNoTracking()
            join family in database.BrowserSessionFamilies.AsNoTracking() on credential.FamilyId equals family.Id
            where credential.Hash == hash
            select new CredentialHint(family.Id, family.UserId, hash)).SingleOrDefaultAsync(cancellationToken);
    }
    private async Task<(BrowserSessionFamily Family, BrowserRefreshCredential Credential)?> FindLockedAsync(CredentialHint? hint, CancellationToken cancellationToken)
    {
        if (hint is null) return null;
        var family = await LockFamilyAsync(hint.FamilyId, cancellationToken);
        if (family is null || family.UserId != hint.UserId) return null;
        var credential = await database.BrowserRefreshCredentials.SingleOrDefaultAsync(x => x.Hash == hint.Hash, cancellationToken);
        if (credential is null) return null;
        await database.Entry(credential).ReloadAsync(cancellationToken);
        return credential.FamilyId == family.Id && credential.Hash == hint.Hash ? (family, credential) : null;
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
            if (key.Length < 32) throw new SessionFailure("hash_key_unavailable");
            return Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(secret)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    private async Task<BrowserSessionResult> Once(string operationName, Func<Task<BrowserSessionResult>> operation, CancellationToken cancellationToken)
    {
        var attempted = false;
        try
        {
            // The configured provider retries reads elsewhere. NEVER replay a session mutation after uncertain commit.
            return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                if (attempted) throw new SessionFailure("database_uncertain");
                attempted = true;
                return await operation();
            });
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            var category = error switch
            {
                SessionFailure failure => failure.Category,
                IOException or UnauthorizedAccessException => "hash_key_unavailable",
                System.Data.Common.DbException or DbUpdateException
                    or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException => "database_uncertain",
                Zeka.Authentication.AuthenticationAuthorityUnavailableException => "issuer_unavailable",
                _ => "unexpected"
            };
            Failures.Add(1, new KeyValuePair<string, object?>("operation", operationName),
                new KeyValuePair<string, object?>("category", category));
            Activity.Current?.AddEvent(new ActivityEvent("browser_session.unavailable", tags: new ActivityTagsCollection
                { { "operation", operationName }, { "category", category } }));
            return Unavailable();
        }
    }
    private static bool Eligible(User user, DateTimeOffset now) => user.Status == UserStatus.Active
        && user.EmailConfirmed && (!user.LockoutEnabled || user.LockoutEnd is null || user.LockoutEnd <= now);
    private static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
    private static DateTimeOffset Deadline(BrowserSessionFamily family) => Min(family.IdleExpiresAtUtc, family.AbsoluteExpiresAtUtc);
    private BrowserSessionResult Success(BrowserSessionFamily family, string token, string secret, DateTimeOffset now) => new()
    { Outcome = BrowserSessionOutcome.Success, SubjectId = family.UserId, AccessToken = token, RefreshSecret = secret,
        ExpiresAtUtc = new DateTimeOffset(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).ValidTo),
        CookieExpiresAtUtc = Deadline(family) };
    private static BrowserSessionResult Denied() => new() { Outcome = BrowserSessionOutcome.Unauthenticated };
    private static BrowserSessionResult Unavailable() => new() { Outcome = BrowserSessionOutcome.Unavailable };
}
