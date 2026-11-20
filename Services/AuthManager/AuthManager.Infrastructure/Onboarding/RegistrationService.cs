using System.Net.Mail;
using System.Security.Cryptography;
using AuthManager.Application.Onboarding;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AuthManager.Infrastructure.Onboarding;

internal sealed class RegistrationService(
    AuthDbContext database,
    UserManager<User> users,
    VerificationTokenService tokens,
    RegistrationOptions options,
    IServiceScopeFactory scopes,
    RegistrationIdentityCancellation identityCancellation,
    TimeProvider clock) : IRegistrationService
{
    private static readonly TimeSpan VerificationLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(10);

    public string? NormalizeAddress(string? address)
        => ValidAddress(address) ? users.NormalizeEmail(address!.Trim()) : null;

    public Guid? ReadChallengeId(string? token)
        => VerificationTokenService.TryRead(token, out var identity) ? identity.ChallengeId : null;

    public async Task<RegistrationOutcome> RegisterAsync(RegistrationRequest input, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return RegistrationOutcome.Unavailable;
        using var cancellation = identityCancellation.Bind(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeAddress(input.Email);
        if (normalized is null || !ValidName(input.FirstName) || !ValidName(input.LastName)
            || input.Password is not { Length: > 0 and <= 256 })
            return RegistrationOutcome.Invalid;

        var ids = LogicalOperation.Create();
        var email = input.Email!.Trim();
        var first = input.FirstName!.Trim();
        var last = input.LastName!.Trim();
        var now = clock.GetUtcNow();
        var draft = NewIdentity(ids.UserId, email, first, last, now);
        foreach (var validator in users.PasswordValidators)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validation = await validator.ValidateAsync(users, draft, input.Password);
            cancellationToken.ThrowIfCancellationRequested();
            if (!validation.Succeeded) return RegistrationOutcome.Invalid;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // One Identity hash for all admitted addresses. Store it only for a newly created user.
        var passwordHash = users.PasswordHasher.HashPassword(draft, input.Password);
        cancellationToken.ThrowIfCancellationRequested();
        return await RunAsync(ids, async (operation, ct) =>
        {
            if (await IsRegisteredAsync(database, normalized, ct))
                return RegistrationOutcome.Accepted;
            var user = NewIdentity(ids.UserId, email, first, last, now);
            user.PasswordHash = passwordHash;
            await CheckIdentityAsync(() => users.CreateAsync(user), ct);
            AddChallenge(operation, OnboardingOperationKind.Register, user, normalized, 1, now);
            return RegistrationOutcome.Accepted;
        }, cancellationToken);
    }

    internal static Task<bool> IsRegisteredAsync(AuthDbContext database, string normalized, CancellationToken cancellationToken)
        => database.Users.AsNoTracking().AnyAsync(user => user.NormalizedEmail == normalized, cancellationToken);

    public async Task<RegistrationOutcome> ResendAsync(string email, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return RegistrationOutcome.Unavailable;
        using var cancellation = identityCancellation.Bind(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeAddress(email);
        if (normalized is null) return RegistrationOutcome.Invalid;

        return await RunAsync(LogicalOperation.Create(), async (operation, ct) =>
        {
            var now = clock.GetUtcNow();
            var user = await database.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == normalized, ct);
            ct.ThrowIfCancellationRequested();
            if (!Pending(user)) return RegistrationOutcome.Accepted;

            var addressBinding = VerificationTokenService.AddressBinding(normalized);
            var recent = await database.EmailVerificationChallenges.AsNoTracking()
                .Where(row => row.AddressBinding == addressBinding && row.CreatedAtUtc > now - VerificationLifetime)
                .OrderByDescending(row => row.CreatedAtUtc).Take(5).Select(row => row.CreatedAtUtc).ToListAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (recent.Count >= 5 || recent.Count(x => x > now.AddHours(-1)) >= 3
                || recent.Any(x => x > now.AddMinutes(-1)))
                return RegistrationOutcome.Accepted;

            var previous = await database.EmailVerificationChallenges
                .Where(row => row.UserId == user!.Id)
                .OrderByDescending(row => row.Generation).FirstOrDefaultAsync(ct);
            // Identity's concurrency-stamped UPDATE acquires the PostgreSQL user row first.
            // A stale read cannot update the challenge after another operation changed the user.
            await CheckIdentityAsync(() => users.UpdateAsync(user!), ct);
            if (previous is { Status: VerificationChallengeState.Pending })
            {
                previous.Transition(VerificationChallengeState.Superseded, now, operation.Id);
                // Release the partial unique index inside this transaction before inserting the replacement.
                ct.ThrowIfCancellationRequested();
                await database.SaveChangesAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            AddChallenge(operation, OnboardingOperationKind.Resend, user!, normalized,
                checked((previous?.Generation ?? 0) + 1), now);
            return RegistrationOutcome.Accepted;
        }, cancellationToken);
    }

    public async Task<VerificationExchange> ExchangeAsync(string token, CancellationToken cancellationToken)
    {
        if (!options.Enabled) return new() { Outcome = RegistrationOutcome.Unavailable };
        using var cancellation = identityCancellation.Bind(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!VerificationTokenService.TryRead(token, out var identity))
            return new() { Outcome = RegistrationOutcome.Invalid };

        var ids = LogicalOperation.Create();
        var bytes = RandomNumberGenerator.GetBytes(32);
        var credential = WebEncoders.Base64UrlEncode(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        var result = await RunAsync(ids, async (operation, ct) =>
        {
            var now = clock.GetUtcNow();
            var challenge = await database.EmailVerificationChallenges.SingleOrDefaultAsync(row => row.Id == identity.ChallengeId, ct);
            ct.ThrowIfCancellationRequested();
            if (challenge is null || challenge.Status != VerificationChallengeState.Pending
                || challenge.ExpiresAtUtc <= now || challenge.Generation != identity.Generation
                || challenge.KeyId != identity.KeyId || !tokens.Matches(challenge, token))
                return RegistrationOutcome.Invalid;

            var user = await database.Users.SingleOrDefaultAsync(row => row.Id == challenge.UserId, ct);
            ct.ThrowIfCancellationRequested();
            if (!Pending(user) || !BindingsMatch(challenge, user!))
                return RegistrationOutcome.Invalid;

            // The HMAC challenge authorizes this operation. Use Identity's ordinary confirmation
            // API and configured provider locally; its internal token never leaves this process.
            ct.ThrowIfCancellationRequested();
            var confirmation = await users.GenerateEmailConfirmationTokenAsync(user!);
            await CheckIdentityAsync(() => users.ConfirmEmailAsync(user!, confirmation), ct);
            user!.ActivateConfirmedIdentity(now);
            await CheckIdentityAsync(() => users.UpdateAsync(user), ct);
            challenge.Transition(VerificationChallengeState.Consumed, now, operation.Id);
            operation.ExpiresAtUtc = now.Add(GrantLifetime);
            database.OnboardingGrants.Add(new()
            {
                Id = operation.GrantId, ChallengeId = challenge.Id, UserId = user.Id,
                CredentialHash = VerificationTokenService.GrantHash(credential),
                StampBinding = VerificationTokenService.StampBinding(user.SecurityStamp!),
                ExpiresAtUtc = operation.ExpiresAtUtc.Value
            });
            AddReceipt(operation, OnboardingOperationKind.Exchange, challenge, null, operation.GrantId);
            return RegistrationOutcome.Accepted;
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        return result == RegistrationOutcome.Accepted
            ? new() { Outcome = result, Credential = credential, ExpiresAtUtc = ids.ExpiresAtUtc }
            : new() { Outcome = result };
    }

    internal async Task<Guid?> ValidateGrantAsync(string credential, string purpose, CancellationToken cancellationToken)
    {
        if (!options.Enabled || purpose != OnboardingGrant.OrganisationPurpose
            || !VerificationTokenService.IsGrantCredential(credential))
            return null;
        var hash = VerificationTokenService.GrantHash(credential);
        var grant = await database.OnboardingGrants.AsNoTracking().SingleOrDefaultAsync(row => row.CredentialHash == hash, cancellationToken);
        if (grant is null || grant.Purpose != purpose || grant.ConsumedAtUtc is not null
            || grant.RevokedAtUtc is not null || grant.ExpiresAtUtc <= clock.GetUtcNow())
            return null;
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == grant.UserId, cancellationToken);
        return user is { Status: UserStatus.Active, EmailConfirmed: true, SecurityStamp: { } stamp }
            && VerificationTokenService.FixedEqual(grant.StampBinding, VerificationTokenService.StampBinding(stamp))
            ? user.Id : null;
    }

    private async Task<RegistrationOutcome> RunAsync(LogicalOperation state,
        Func<LogicalOperation, CancellationToken, Task<RegistrationOutcome>> work, CancellationToken cancellationToken)
    {
        // Only known optimistic conflicts may re-evaluate once. The provider handles its
        // configured transient retries and independently verifies any uncertain commit.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await database.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(state,
                    async (operation, ct) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        database.ChangeTracker.Clear();
                        operation.Receipt = null;
                        operation.ExpiresAtUtc = null;
                        if (database.Database.CurrentTransaction is null) throw new IdentityWriteRejected();
                        var result = await work(operation, ct);
                        ct.ThrowIfCancellationRequested();
                        if (operation.Receipt is not null)
                            await database.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken: ct);
                        // EF owns the transaction and commits only after this delegate returns.
                        ct.ThrowIfCancellationRequested();
                        return result;
                    }, VerifyCommitAsync, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return outcome;
            }
            catch (Exception ex) when (IsKnownConflict(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt == 1) return RegistrationOutcome.Unavailable;
            }
            catch (Exception ex) when (ex is NpgsqlException or DbUpdateException or OutcomeUnknown
                or IdentityWriteRejected or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RegistrationOutcome.Unavailable;
            }
            finally
            {
                database.ChangeTracker.Clear();
            }
        }
        return RegistrationOutcome.Unavailable;
    }

    private async Task<bool> VerifyCommitAsync(LogicalOperation operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = operation.Receipt;
        if (expected is null) return true;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var reader = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            if (ReferenceEquals(reader, database)) throw new OutcomeUnknown();
            var receipt = await reader.OnboardingOperationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == operation.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (receipt is null) return false;
            if (receipt.Kind != expected.Kind || receipt.UserId != expected.UserId
                || receipt.ChallengeId != expected.ChallengeId || receipt.Generation != expected.Generation
                || receipt.OutboxId != expected.OutboxId || receipt.GrantId != expected.GrantId)
                throw new OutcomeUnknown();

            var valid = receipt.Kind == OnboardingOperationKind.Exchange
                ? await (from challenge in reader.EmailVerificationChallenges.AsNoTracking()
                         join user in reader.Users.AsNoTracking() on challenge.UserId equals user.Id
                         join grant in reader.OnboardingGrants.AsNoTracking() on challenge.Id equals grant.ChallengeId
                         where challenge.Id == receipt.ChallengeId && challenge.UserId == receipt.UserId
                             && challenge.Generation == receipt.Generation && challenge.Version == operation.Id
                             && challenge.Status == VerificationChallengeState.Consumed
                             && user.EmailConfirmed && user.Status == UserStatus.Active
                             && grant.Id == receipt.GrantId && grant.UserId == receipt.UserId
                             && grant.ExpiresAtUtc == operation.ExpiresAtUtc && grant.Purpose == OnboardingGrant.OrganisationPurpose
                             && grant.ConsumedAtUtc == null && grant.RevokedAtUtc == null
                         select grant.Id).AnyAsync(cancellationToken)
                : await (from challenge in reader.EmailVerificationChallenges.AsNoTracking()
                         join user in reader.Users.AsNoTracking() on challenge.UserId equals user.Id
                         join message in reader.VerificationMailOutboxMessages.AsNoTracking() on challenge.Id equals message.ChallengeId
                         where challenge.Id == receipt.ChallengeId && challenge.UserId == receipt.UserId
                             && challenge.Generation == receipt.Generation
                             && user.Id == receipt.UserId && message.Id == receipt.OutboxId
                             && message.OperationId == operation.Id && message.Generation == receipt.Generation
                             && message.PayloadVersion == VerificationMailOutboxMessage.SupportedVersion
                         select message.Id).AnyAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return valid ? true : throw new OutcomeUnknown();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutcomeUnknown)
        {
            // Never permit provider retries after inability to prove whether a write committed.
            throw new OutcomeUnknown();
        }
    }

    private void AddChallenge(LogicalOperation operation, OnboardingOperationKind kind,
        User user, string normalized, int generation, DateTimeOffset now)
    {
        if (user.SecurityStamp is not { Length: > 0 } stamp) throw new IdentityWriteRejected();
        var challenge = new EmailVerificationChallenge
        {
            Id = operation.ChallengeId, UserId = user.Id, Generation = generation,
            KeyId = tokens.ActiveKeyId, AddressBinding = VerificationTokenService.AddressBinding(normalized),
            StampBinding = VerificationTokenService.StampBinding(stamp), TokenHash = string.Empty,
            CreatedAtUtc = now, ExpiresAtUtc = now.Add(VerificationLifetime)
        };
        challenge.TokenHash = VerificationTokenService.TokenHash(tokens.Create(challenge) ?? throw new IdentityWriteRejected());
        database.EmailVerificationChallenges.Add(challenge);
        database.VerificationMailOutboxMessages.Add(new()
        {
            Id = operation.OutboxId, OperationId = operation.Id, ChallengeId = challenge.Id,
            Generation = generation, EnqueuedAtUtc = now, NextAttemptAtUtc = now, Version = operation.Id
        });
        AddReceipt(operation, kind, challenge, operation.OutboxId, null);
    }

    private void AddReceipt(LogicalOperation operation, OnboardingOperationKind kind,
        EmailVerificationChallenge challenge, Guid? outboxId, Guid? grantId)
    {
        var receipt = new OnboardingOperationReceipt
        {
            Id = operation.Id, Kind = kind, UserId = challenge.UserId, ChallengeId = challenge.Id,
            Generation = challenge.Generation, OutboxId = outboxId, GrantId = grantId
        };
        database.OnboardingOperationReceipts.Add(receipt);
        operation.Receipt = receipt;
    }

    internal static bool BindingsMatch(EmailVerificationChallenge challenge, User user)
        => user.NormalizedEmail is { Length: > 0 } address && user.SecurityStamp is { Length: > 0 } stamp
        && VerificationTokenService.FixedEqual(challenge.AddressBinding, VerificationTokenService.AddressBinding(address))
        && VerificationTokenService.FixedEqual(challenge.StampBinding, VerificationTokenService.StampBinding(stamp));

    private static bool Pending(User? user)
        => user is { Status: UserStatus.PendingVerification, EmailConfirmed: false };

    private static bool ValidName(string? value)
        => value is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);

    private bool ValidAddress(string? value)
    {
        if (value is not { Length: > 0 and <= 256 }) return false;
        var email = value.Trim();
        var allowed = users.Options.User.AllowedUserNameCharacters;
        return MailAddress.TryCreate(email, out var parsed) && parsed.Address == email
            && (string.IsNullOrEmpty(allowed) || email.All(allowed.Contains));
    }

    private static User NewIdentity(Guid id, string email, string first, string last, DateTimeOffset now)
    {
        var user = User.Create(email, email, first, last, now);
        user.Id = id;
        return user;
    }

    private static async Task CheckIdentityAsync(Func<Task<IdentityResult>> write, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await write();
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Succeeded) return;
        if (result.Errors.Any(e => e.Code is "ConcurrencyFailure" or "DuplicateEmail" or "DuplicateUserName"))
            throw new KnownConflict();
        throw new IdentityWriteRejected();
    }

    private static bool IsKnownConflict(Exception error)
        => error is KnownConflict or DbUpdateConcurrencyException
            || error is DbUpdateException { InnerException: PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "EmailIndex" or "UserNameIndex" } };

    private sealed class KnownConflict : Exception;
    private sealed class IdentityWriteRejected : Exception;
    private sealed class OutcomeUnknown : Exception;

    private sealed class LogicalOperation
    {
        public static LogicalOperation Create() => new();
        public Guid Id { get; } = Guid.NewGuid();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid ChallengeId { get; } = Guid.NewGuid();
        public Guid OutboxId { get; } = Guid.NewGuid();
        public Guid GrantId { get; } = Guid.NewGuid();
        public DateTimeOffset? ExpiresAtUtc { get; set; }
        public OnboardingOperationReceipt? Receipt { get; set; }
    }
}

internal sealed class RegistrationIdentityCancellation
{
    public CancellationToken? Token { get; private set; }

    public IDisposable Bind(CancellationToken token)
    {
        var binding = new Binding(this, Token);
        Token = token;
        return binding;
    }

    private sealed class Binding(RegistrationIdentityCancellation owner, CancellationToken? previous) : IDisposable
    {
        public void Dispose() => owner.Token = previous;
    }
}

internal sealed class RegistrationUserManager(
    IUserStore<User> store,
    IOptions<IdentityOptions> options,
    IPasswordHasher<User> passwordHasher,
    IEnumerable<IUserValidator<User>> userValidators,
    IEnumerable<IPasswordValidator<User>> passwordValidators,
    ILookupNormalizer normalizer,
    IdentityErrorDescriber errors,
    IServiceProvider services,
    ILogger<UserManager<User>> logger,
    RegistrationIdentityCancellation identityCancellation)
    : AspNetUserManager<User>(store, options, passwordHasher, userValidators, passwordValidators,
        normalizer, errors, services, logger)
{
    protected override CancellationToken CancellationToken => identityCancellation.Token ?? base.CancellationToken;
}

// Keeps readiness tied to the real restricted Auth store used by registration.
public sealed class RegistrationReadiness(IServiceScopeFactory scopes) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private const string ProbeAddress = "REGISTRATION-READINESS@EXAMPLE.INVALID";
    private bool ready;
    public bool IsReady => Volatile.Read(ref ready);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Allow the listener to report live and unready even during initial model construction.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                using (var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    attempt.CancelAfter(ProbeTimeout);
                    try
                    {
                        await ProbeAsync(attempt.Token);
                        Volatile.Write(ref ready, !attempt.IsCancellationRequested);
                    }
                    catch (Exception)
                    {
                        // The health endpoint reports only dependency availability, never provider details.
                        Volatile.Write(ref ready, false);
                    }
                }
                await Task.Delay(ProbeInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref ready, false);
        }
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<AuthDbContext>();
        var manager = services.GetRequiredService<UserManager<User>>();
        using var cancellation = services.GetRequiredService<RegistrationIdentityCancellation>().Bind(cancellationToken);

        // Exercise the exact absent-address test and Identity's normalized lookup shapes.
        _ = await RegistrationService.IsRegisteredAsync(database, ProbeAddress, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await manager.FindByNameAsync(ProbeAddress);
        cancellationToken.ThrowIfCancellationRequested();
        _ = await manager.FindByEmailAsync(ProbeAddress);
        cancellationToken.ThrowIfCancellationRequested();

        // Bind columns and SELECT privileges even on an empty schema. No rows or accounts are created.
        _ = await database.EmailVerificationChallenges.AnyAsync(row => row.Id == Guid.Empty, cancellationToken);
        _ = await database.VerificationMailOutboxMessages.AnyAsync(row => row.Id == Guid.Empty, cancellationToken);
        _ = await database.OnboardingOperationReceipts.AnyAsync(row => row.Id == Guid.Empty, cancellationToken);
        _ = await database.OnboardingGrants.AnyAsync(row => row.Id == Guid.Empty, cancellationToken);
    }
}
