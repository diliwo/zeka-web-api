using System.Net.Mail;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.Onboarding;

internal sealed class VerificationMailDispatcher(AuthDbContext database, VerificationTokenService tokens,
    IVerificationMailDelivery delivery, TimeProvider clock)
{
    private const int MaximumAttempts = 4;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SmtpDeadline = TimeSpan.FromSeconds(5);

    public async Task<bool> DispatchOneAsync(CancellationToken stoppingToken)
    {
        var now = clock.GetUtcNow();
        var message = await database.VerificationMailOutboxMessages
            .Where(row => ((row.Status == VerificationMailState.Pending || row.Status == VerificationMailState.RetryScheduled)
                           && row.NextAttemptAtUtc <= now)
                || (row.Status == VerificationMailState.Leased && row.LeaseExpiresAtUtc <= now))
            .OrderBy(row => row.NextAttemptAtUtc).ThenBy(row => row.Id).FirstOrDefaultAsync(stoppingToken);
        if (message is null) return false;

        var leaseId = Guid.NewGuid();
        message.Status = VerificationMailState.Leased;
        message.LeaseId = leaseId;
        message.LeaseExpiresAtUtc = now.Add(LeaseDuration);
        if (!await UpdateAsync(message, stoppingToken)) return true;

        var decision = await EvaluateAsync(message, stoppingToken);
        if (message.LeaseExpiresAtUtc <= clock.GetUtcNow()) return true;
        if (decision.Terminal is { } terminal)
        {
            await FinishAsync(message, leaseId, terminal, decision.Reason, false, stoppingToken);
            return true;
        }
        if (message.Attempts >= MaximumAttempts)
        {
            await FinishAsync(message, leaseId, VerificationMailState.DeadLettered, "ATTEMPTS_EXHAUSTED", false, stoppingToken);
            return true;
        }

        // Renew just before submission. The optimistic condition binds this write to the
        // acquired row version and the lease lasts longer than the hard SMTP deadline.
        message.Attempts++;
        message.LeaseExpiresAtUtc = clock.GetUtcNow().Add(LeaseDuration);
        if (!await UpdateAsync(message, stoppingToken)) return true;

        // Re-evaluate after leasing so writes that happened during lease acquisition
        // cannot send a stale challenge. A subsequently racing resend remains safe.
        decision = await EvaluateAsync(message, stoppingToken);
        if (message.LeaseExpiresAtUtc <= clock.GetUtcNow()) return true;
        if (decision.Terminal is { } rechecked)
        {
            await FinishAsync(message, leaseId, rechecked, decision.Reason, false, stoppingToken);
            return true;
        }

        using var smtpTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        smtpTimeout.CancelAfter(SmtpDeadline);
        try
        {
            await delivery.SendAsync(decision.Recipient!, decision.Token!, smtpTimeout.Token);
        }
        catch (Exception error) when (error is SmtpException or IOException or OperationCanceledException)
        {
            if (stoppingToken.IsCancellationRequested) return true;
            var next = message.Attempts >= MaximumAttempts ? VerificationMailState.DeadLettered : VerificationMailState.RetryScheduled;
            await FinishAsync(message, leaseId, next, "SMTP_OUTCOME_UNCONFIRMED", false, stoppingToken);
            return true;
        }

        var acceptedAt = clock.GetUtcNow();
        var afterAcceptance = await EvaluateAsync(message, stoppingToken);
        await FinishAsync(message, leaseId, VerificationMailState.Delivered, "SMTP_ACCEPTED",
            afterAcceptance.Terminal is not null, stoppingToken, acceptedAt);
        return true;
    }

    private async Task<DeliveryDecision> EvaluateAsync(VerificationMailOutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.PayloadVersion != VerificationMailOutboxMessage.SupportedVersion)
            return new(VerificationMailState.DeadLettered, "UNSUPPORTED_PAYLOAD", null, null);

        var source = await (from matchingChallenge in database.EmailVerificationChallenges.AsNoTracking()
                            join user in database.Users.AsNoTracking() on matchingChallenge.UserId equals user.Id
                            where matchingChallenge.Id == message.ChallengeId
                            select new { Challenge = matchingChallenge, User = user }).SingleOrDefaultAsync(cancellationToken);
        if (source is null || source.Challenge.Generation != message.Generation)
            return new(VerificationMailState.DeadLettered, "INVALID_CHALLENGE_LINK", null, null);

        var challenge = source.Challenge;
        if (challenge.Status == VerificationChallengeState.Superseded)
            return new(VerificationMailState.Superseded, "SUPERSEDED", null, null);
        if (challenge.Status == VerificationChallengeState.Consumed)
            return new(VerificationMailState.Consumed, "CONSUMED", null, null);
        if (challenge.Status == VerificationChallengeState.Revoked || source.User.Status != UserStatus.PendingVerification
            || source.User.EmailConfirmed || !RegistrationService.BindingsMatch(challenge, source.User))
            return new(VerificationMailState.Revoked, "BINDING_REVOKED", null, null);
        if (challenge.ExpiresAtUtc <= clock.GetUtcNow())
            return new(VerificationMailState.Expired, "EXPIRED", null, null);
        if (source.User.Email is not { } recipient || !MailAddress.TryCreate(recipient, out var address)
            || address.Address != recipient || tokens.Create(challenge) is not { } token
            || !VerificationTokenService.FixedEqual(challenge.TokenHash, VerificationTokenService.TokenHash(token)))
            return new(VerificationMailState.DeadLettered, "INVALID_DELIVERY_BINDING", null, null);

        return new(null, null, recipient, token);
    }

    private async Task FinishAsync(VerificationMailOutboxMessage message, Guid leaseId, VerificationMailState state,
        string? outcome, bool obsoleteAtAcceptance, CancellationToken cancellationToken, DateTimeOffset? acceptedAt = null)
    {
        var now = clock.GetUtcNow();
        if (message.LeaseId != leaseId || message.LeaseExpiresAtUtc <= now) return;
        message.Status = state;
        message.Outcome = outcome;
        message.LeaseId = null;
        message.LeaseExpiresAtUtc = null;
        if (state == VerificationMailState.Delivered)
        {
            message.DeliveredAtUtc = acceptedAt ?? throw new InvalidOperationException("SMTP_ACCEPTANCE_TIME_REQUIRED");
            message.ObsoleteAtAcceptance = obsoleteAtAcceptance;
        }
        if (state == VerificationMailState.RetryScheduled)
            message.NextAttemptAtUtc = now.AddSeconds(10 * message.Attempts);
        await UpdateAsync(message, cancellationToken);
    }

    private async Task<bool> UpdateAsync(VerificationMailOutboxMessage message, CancellationToken cancellationToken)
    {
        message.Version = Guid.NewGuid();
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            database.ChangeTracker.Clear();
            return false;
        }
    }

    private sealed record DeliveryDecision(VerificationMailState? Terminal, string? Reason, string? Recipient, string? Token);
}
