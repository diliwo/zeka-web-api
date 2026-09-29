using AuthManager.Application.Common.Outbox;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthManager.Infrastructure.Outbox;

internal sealed class OutboxDispatcher(
    AuthDbContext dbContext,
    IOutboxMessagePublisher publisher,
    IOutboxPublicationActivityClassifier activityClassifier,
    TimeProvider timeProvider,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxDispatcher> logger) : IOutboxDispatcher
{
    private readonly OutboxDispatcherOptions _options = options.Value;

    public async Task<int> DispatchBatchAsync(CancellationToken cancellationToken = default)
    {
        ValidateOptions();
        var now = timeProvider.GetUtcNow();
        var candidateIds = await CandidateIdsAsync(now, cancellationToken);

        var processed = 0;
        foreach (var messageId in candidateIds)
        {
            if (await TryDispatchAsync(messageId, cancellationToken))
                processed++;
        }

        return processed;
    }

    private async Task<bool> TryDispatchAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var acquired = await AcquireLeaseAsync(messageId, leaseId, now, cancellationToken);

        if (acquired == 0)
            return false;

        var message = await dbContext.OutboxMessages.SingleAsync(
            value => value.Id == messageId && value.LeaseId == leaseId, cancellationToken);
        var envelope = new OutboxMessageEnvelope(message.Id, message.MessageType,
            message.ContractVersion, message.Payload, message.OccurredAtUtc,
            message.CorrelationId, message.OrganisationId);

        try
        {
            if (envelope.OrganisationId is null || IsSqlite())
                return await PublishAsync(message, envelope, cancellationToken);

            return await PublishOrdinaryOrganisationMessageAsync(message, envelope,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failedAt = timeProvider.GetUtcNow();
            var nextAttempt = failedAt.Add(BackoffFor(message.AttemptCount + 1));
            message.MarkFailed(failedAt, nextAttempt, SafeError(exception), _options.MaxAttempts);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (message.DeadLetteredAtUtc is not null)
                logger.LogError("Outbox message {MessageId} ({MessageType}) was dead-lettered after {AttemptCount} attempts.",
                    message.Id, message.MessageType, message.AttemptCount);
            else
                logger.LogWarning("Outbox message {MessageId} ({MessageType}) failed on attempt {AttemptCount}; next attempt at {NextAttemptAtUtc}.",
                    message.Id, message.MessageType, message.AttemptCount, message.NextAttemptAtUtc);

            return false;
        }
    }

    private async Task<bool> PublishAsync(Persistence.Entities.OutboxMessage message,
        OutboxMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        await publisher.PublishAsync(envelope, cancellationToken);
        message.MarkProcessed(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> PublishOrdinaryOrganisationMessageAsync(
        Persistence.Entities.OutboxMessage message,
        OutboxMessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var organisationId = envelope.OrganisationId!.Value;
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_catalog.set_config('zeka.organisation_id', {0}, true)",
            organisationId.ToString("D"));
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended({0}, 0))",
            organisationId.ToString("D"));

        if (await activityClassifier.IsTrustedLifecycleControlAsync(envelope, cancellationToken))
        {
            await publisher.PublishAsync(envelope, cancellationToken);
            message.MarkProcessed(timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        var boundary = await PublicationBoundary(organisationId, cancellationToken);
        if (boundary == OrganisationPublicationBoundary.Archived)
        {
            message.MarkTerminallyRetained(timeProvider.GetUtcNow(), "OrganisationArchived");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Outbox message {MessageId} ({MessageType}) was terminally retained because the organisation is archived.",
                message.Id, message.MessageType);
            return false;
        }

        if (boundary == OrganisationPublicationBoundary.Closing)
        {
            message.Defer(timeProvider.GetUtcNow().Add(_options.InitialRetryDelay),
                "OrganisationLifecycleBoundary");
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Outbox message {MessageId} ({MessageType}) was deferred at the organisation lifecycle boundary.",
                message.Id, message.MessageType);
            return false;
        }

        await publisher.PublishAsync(envelope, cancellationToken);
        message.MarkProcessed(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task<OrganisationPublicationBoundary> PublicationBoundary(Guid organisationId,
        CancellationToken cancellationToken)
    {
        var transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Outbox publication transaction is required.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE
              WHEN o."Status" IN (@archived, @disposition_ready, @purge_in_progress) THEN 2
              WHEN o."Status"=@closing AND EXISTS (
                SELECT 1 FROM public."AuthClosureParticipantExecutions" e
                WHERE e."OrganisationId"=o."Id") THEN 1
              ELSE 0
            END
            FROM public."Organisations" o
            WHERE o."Id"=@organisation_id
            """;
        var organisation = command.CreateParameter();
        organisation.ParameterName = "organisation_id";
        organisation.Value = organisationId;
        command.Parameters.Add(organisation);
        var closing = command.CreateParameter();
        closing.ParameterName = "closing";
        closing.Value = (int)OrganisationStatus.Closing;
        command.Parameters.Add(closing);
        var archived = command.CreateParameter();
        archived.ParameterName = "archived";
        archived.Value = (int)OrganisationStatus.Archived;
        command.Parameters.Add(archived);
        var dispositionReady = command.CreateParameter();
        dispositionReady.ParameterName = "disposition_ready";
        dispositionReady.Value = (int)OrganisationStatus.DispositionReady;
        command.Parameters.Add(dispositionReady);
        var purgeInProgress = command.CreateParameter();
        purgeInProgress.ParameterName = "purge_in_progress";
        purgeInProgress.Value = (int)OrganisationStatus.PurgeInProgress;
        command.Parameters.Add(purgeInProgress);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int state && Enum.IsDefined(typeof(OrganisationPublicationBoundary), state)
            ? (OrganisationPublicationBoundary)state
            : OrganisationPublicationBoundary.Open;
    }

    private async Task<List<Guid>> CandidateIdsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pending = dbContext.OutboxMessages.AsNoTracking()
            .Where(value => value.ProcessedAtUtc == null && value.DeadLetteredAtUtc == null);

        if (IsSqlite())
        {
            // SQLite is used only by the integration-test substitute and cannot translate
            // DateTimeOffset ordering/comparison. PostgreSQL retains the bounded server query.
            return (await pending.ToListAsync(cancellationToken))
                .Where(value => value.NextAttemptAtUtc <= now &&
                                (value.LeaseExpiresAtUtc == null || value.LeaseExpiresAtUtc <= now))
                .OrderBy(value => value.OccurredAtUtc)
                .Take(_options.BatchSize)
                .Select(value => value.Id)
                .ToList();
        }

        return await pending
            .Where(value => value.NextAttemptAtUtc <= now &&
                            (value.LeaseExpiresAtUtc == null || value.LeaseExpiresAtUtc <= now))
            .OrderBy(value => value.OccurredAtUtc)
            .Select(value => value.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);
    }

    private async Task<int> AcquireLeaseAsync(Guid messageId, Guid leaseId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (IsSqlite())
        {
            var candidate = await dbContext.OutboxMessages.SingleOrDefaultAsync(
                value => value.Id == messageId && value.ProcessedAtUtc == null && value.DeadLetteredAtUtc == null,
                cancellationToken);
            if (candidate is null || candidate.NextAttemptAtUtc > now ||
                candidate.LeaseExpiresAtUtc is not null && candidate.LeaseExpiresAtUtc > now)
                return 0;

            candidate.AcquireLease(leaseId, now.Add(_options.LeaseDuration));
            await dbContext.SaveChangesAsync(cancellationToken);
            return 1;
        }

        return await dbContext.OutboxMessages
            .Where(value => value.Id == messageId && value.ProcessedAtUtc == null &&
                            value.DeadLetteredAtUtc == null && value.NextAttemptAtUtc <= now &&
                            (value.LeaseExpiresAtUtc == null || value.LeaseExpiresAtUtc <= now))
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(value => value.LeaseId, leaseId)
                    .SetProperty(value => value.LeaseExpiresAtUtc, now.Add(_options.LeaseDuration)),
                cancellationToken);
    }

    private bool IsSqlite() =>
        string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal);

    private TimeSpan BackoffFor(int attempt)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var ticks = Math.Min(_options.InitialRetryDelay.Ticks * multiplier, _options.MaxRetryDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static string SafeError(Exception exception) =>
        exception.GetType().Name.Length <= 2000 ? exception.GetType().Name : "PublishFailure";

    private void ValidateOptions()
    {
        if (_options.BatchSize < 1 || _options.MaxAttempts < 1 || _options.LeaseDuration <= TimeSpan.Zero ||
            _options.InitialRetryDelay <= TimeSpan.Zero || _options.MaxRetryDelay < _options.InitialRetryDelay)
            throw new InvalidOperationException("Outbox dispatcher options are invalid.");
    }

    private enum OrganisationPublicationBoundary
    {
        Open = 0,
        Closing = 1,
        Archived = 2
    }
}
