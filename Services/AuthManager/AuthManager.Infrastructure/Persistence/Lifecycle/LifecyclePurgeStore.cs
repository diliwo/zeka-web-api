using System.Data;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

/// <summary>
/// Explicit test-harness capability. It is never registered by a production API or scheduler.
/// Its environment guard is defense in depth, not a substitute for production authority design.
/// </summary>
public sealed class PurgeFixtureScope(Guid organisationId, string environmentName)
    : INonProductionPurgeCapability
{
    public Guid OrganisationId { get; } = organisationId != Guid.Empty
        && environmentName == "Testing"
        && !string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Production", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
            "Production", StringComparison.OrdinalIgnoreCase)
            ? organisationId
            : throw new InvalidOperationException("Purge fixture capability requires an isolated test organisation.");

    public void Demand(Guid requested)
    {
        if (requested != OrganisationId)
            throw new InvalidOperationException("Purge fixture capability is bound to one organisation.");
    }
}

/// <summary>Fixture-only irreversible admission through the normal Auth persistence boundary.</summary>
public sealed class LifecyclePurgeStore(DbContextOptions<AuthDbContext> options,
    TimeProvider clock, PurgeFixtureScope fixtureScope) : ILifecyclePurgeStore
{
    public async Task<IReadOnlyList<PurgeMessageV1>> ReadAdmittedMessagesAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken)
    {
        fixtureScope.Demand(organisationId);
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        try
        {
            await EstablishContext(database, transaction, organisationId, cancellationToken);
            var operation = await database.Set<LifecycleOperation>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
            var plan = await database.LifecyclePurgePlans.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
            if (operation is null || plan is null || operation.PurgePlanHash != plan.PlanHash
                || operation.RegistryRevision != plan.RegistryRevision
                || operation.IrreversibleRevision is null || operation.IrreversibleStartedAt is null
                || operation.State is not (LifecycleOperationState.PurgeInProgress
                    or LifecycleOperationState.PurgeExecutionComplete))
                throw new InvalidOperationException("No admitted purge outbox exists for this operation.");
            var rows = await database.LifecyclePurgeOutbox.AsNoTracking()
                .Where(x => x.OperationId == operationId)
                .ToArrayAsync(cancellationToken);
            var messages = rows.Select(x => x.Decode())
                .OrderBy(x => x.Kind).ThenBy(x => x.Category, StringComparer.Ordinal).ToArray();
            var expected = plan.Entries.Where(x => x.Decision == RetentionDecisionCode.Purge).ToArray();
            if (messages.Length != expected.Length + 1
                || messages.Count(x => x.Kind == PurgeMessageKind.IrreversibleStarted) != 1
                || messages[0].MessageId != PurgeMessageV1.StartId(plan.Id)
                || messages[0].ParticipantId != "" || messages[0].Category != ""
                || messages[0].ItemId != "" || messages[0].IdempotencyId != messages[0].MessageId
                || messages[0].CapabilityKey != "organisation.purge-started"
                || messages.Any(x => x.OrganisationId != organisationId
                    || x.TerminationOperationId != operationId
                    || x.OperationRevision != operation.IrreversibleRevision
                    || x.RegistryRevision != plan.RegistryRevision || x.PlanId != plan.Id
                    || x.PlanHash != plan.PlanHash || x.DecisionSetId != plan.DecisionSetId
                    || x.DecisionSetHash != plan.DecisionSetHash)
                || expected.Any(entry => messages.Count(x => x.Kind == PurgeMessageKind.ParticipantPurge
                    && x.ParticipantId == entry.ParticipantId && x.Category == entry.Category
                    && x.ItemId == entry.ItemId
                    && x.CapabilityKey == ReviewedDispositionRegistryV1.CapabilityKey
                    && x.MessageId == PurgeMessageV1.CommandId(plan.Id,
                        entry.ParticipantId, entry.Category, entry.ItemId)) != 1))
                throw new InvalidOperationException("Frozen purge outbox differs from the admitted plan.");
            await transaction.CommitAsync(cancellationToken);
            return messages;
        }
        finally { ResetContext(database); }
    }

    public async Task<PurgeReceiptResult> RecordReceiptAsync(PurgeReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        fixtureScope.Demand(receipt.OrganisationId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                try
                {
                    await EstablishContext(database, transaction, receipt.OrganisationId,
                        cancellationToken);
                    var result = await RecordInTransaction(database, receipt, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                finally { ResetContext(database); }
            }
            catch (DbUpdateConcurrencyException) { }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        return new(PurgeReceiptStatus.Unavailable, receipt.TerminationOperationId, receipt.Category);
    }

    private async Task<PurgeReceiptResult> RecordInTransaction(AuthDbContext database,
        PurgeReceiptV1 receipt, CancellationToken cancellationToken)
    {
        var operation = await database.Set<LifecycleOperation>().SingleOrDefaultAsync(
            x => x.Id == receipt.TerminationOperationId, cancellationToken);
        var plan = await database.LifecyclePurgePlans.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == receipt.PlanId && x.OperationId == receipt.TerminationOperationId,
            cancellationToken);
        if (operation is null || plan is null || operation.OrganisationId != receipt.OrganisationId
            || operation.IrreversibleRevision != receipt.IrreversibleRevision
            || operation.PurgePlanHash != plan.PlanHash
            || operation.State is not (LifecycleOperationState.PurgeInProgress
                or LifecycleOperationState.PurgeExecutionComplete))
            return new(PurgeReceiptStatus.Conflict, receipt.TerminationOperationId, receipt.Category);
        var allProgress = await database.LifecyclePurgeProgress
            .Where(x => x.OperationId == operation.Id).ToArrayAsync(cancellationToken);
        var expected = plan.Entries.Where(x => x.Decision == RetentionDecisionCode.Purge).ToArray();
        if (allProgress.Length != expected.Length || expected.Any(entry =>
                allProgress.Count(x => x.Category == entry.Category
                    && x.ItemId == entry.ItemId && x.ParticipantId == entry.ParticipantId
                    && x.PlanId == plan.Id
                    && x.CommandMessageId == PurgeMessageV1.CommandId(plan.Id,
                        entry.ParticipantId, entry.Category, entry.ItemId)) != 1))
            return new(PurgeReceiptStatus.Conflict, operation.Id, receipt.Category);
        var progress = allProgress.SingleOrDefault(x => x.Category == receipt.Category);
        if (progress is null)
            return new(PurgeReceiptStatus.Conflict, operation.Id, receipt.Category);
        var replay = progress.LastReceiptId == receipt.ReceiptId;
        if (operation.State == LifecycleOperationState.PurgeExecutionComplete && !replay)
            return new(PurgeReceiptStatus.Conflict, operation.Id, receipt.Category);
        try
        {
            if (!progress.Record(receipt, plan, operation.IrreversibleRevision.Value,
                    clock.GetUtcNow()))
                return new(PurgeReceiptStatus.Conflict, operation.Id, receipt.Category);
        }
        catch (InvalidOperationException)
        {
            return new(PurgeReceiptStatus.Conflict, operation.Id, receipt.Category);
        }
        if (replay)
            return new(PurgeReceiptStatus.Replay, operation.Id, receipt.Category,
                operation.State == LifecycleOperationState.PurgeExecutionComplete);
        if (progress.State is PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent)
        {
            var otherIncomplete = allProgress.Any(x => x.Category != receipt.Category
                && x.State is not (PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent));
            if (!otherIncomplete && operation.State == LifecycleOperationState.PurgeInProgress)
                operation.MarkPurgeExecutionComplete(clock.GetUtcNow());
        }
        await database.SaveChangesAsync(cancellationToken);
        return new(PurgeReceiptStatus.Recorded, operation.Id, receipt.Category,
            operation.State == LifecycleOperationState.PurgeExecutionComplete);
    }

    public async Task<PurgeAdmissionResult> AdmitAsync(PurgeAdmissionRequest request,
        CancellationToken cancellationToken)
    {
        fixtureScope.Demand(request.OrganisationId);
        if (request.PlanId == Guid.Empty || request.OperationId == Guid.Empty
            || request.OrganisationId == Guid.Empty || request.ExpectedOperationRevision < 1
            || request.ExpectedDecisionSetId == Guid.Empty
            || !Sha256(request.ExpectedDecisionSetHash))
            return Result(PurgeAdmissionStatus.Conflict, request);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                try
                {
                    await EstablishContext(database, transaction, request.OrganisationId,
                        cancellationToken);
                    var result = await AdmitInTransaction(database, request, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return result;
                }
                finally { ResetContext(database); }
            }
            catch (DbUpdateConcurrencyException) { }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) { }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        return Result(PurgeAdmissionStatus.Unavailable, request);
    }

    private async Task<PurgeAdmissionResult> AdmitInTransaction(AuthDbContext database,
        PurgeAdmissionRequest request, CancellationToken cancellationToken)
    {
        var operation = await database.Set<LifecycleOperation>().Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == request.OperationId, cancellationToken);
        if (operation is null || operation.OrganisationId != request.OrganisationId)
            return Result(PurgeAdmissionStatus.Conflict, request);
        var existing = await database.LifecyclePurgePlans.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OperationId == request.OperationId, cancellationToken);
        if (existing is not null)
            return existing.Id == request.PlanId
                && existing.DecisionSetId == request.ExpectedDecisionSetId
                && existing.DecisionSetHash == request.ExpectedDecisionSetHash
                && existing.AdmittedOperationRevision == request.ExpectedOperationRevision
                && operation.PurgePlanHash == existing.PlanHash
                && operation.State is LifecycleOperationState.PurgeInProgress
                    or LifecycleOperationState.PurgeExecutionComplete
                    ? new PurgeAdmissionResult(PurgeAdmissionStatus.Replay, operation.Id,
                        operation.Revision, existing.Id, existing.PlanHash)
                    : Result(PurgeAdmissionStatus.Conflict, request, operation.Revision);

        if (operation.Family != LifecycleOperationFamily.Termination
            || operation.State != LifecycleOperationState.DispositionReady || !operation.IsActive
            || operation.Revision != request.ExpectedOperationRevision
            || operation.RegistryRevision != ReviewedDispositionRegistryV1.Revision
            || operation.DispositionInventoryHash is null
            || operation.RetentionDecisionSetHash != request.ExpectedDecisionSetHash
            || operation.CompletedAt is not null || operation.IrreversibleStartedAt is not null)
            return Result(PurgeAdmissionStatus.Conflict, request, operation.Revision);
        var organisation = await database.Organisations.SingleOrDefaultAsync(
            x => x.Id == request.OrganisationId, cancellationToken);
        if (organisation?.Status != OrganisationStatus.DispositionReady)
            return Result(PurgeAdmissionStatus.Conflict, request, operation.Revision);
        var set = await database.RetentionDecisionSets.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.ExpectedDecisionSetId && x.OperationId == operation.Id,
            cancellationToken);
        if (set is null || set.SetHash != request.ExpectedDecisionSetHash)
            return Result(PurgeAdmissionStatus.Blocked, request, operation.Revision);
        var decisions = await database.RetentionDecisionRecords.AsNoTracking()
            .Where(x => x.SetId == set.Id).ToArrayAsync(cancellationToken);
        var at = clock.GetUtcNow();
        LifecyclePurgePlan plan;
        try { plan = LifecyclePurgePlan.Create(request.PlanId, operation, set, decisions, at); }
        catch (InvalidOperationException) { return Result(PurgeAdmissionStatus.Blocked, request, operation.Revision); }

        // This is the last read/clock guard before writing the irreversible transaction.
        // A changed revision, plan or decision identity cannot be substituted by a caller.
        var beforeWrite = clock.GetUtcNow();
        if (beforeWrite < at || decisions.Any(x => x.ValidUntil <= beforeWrite)
            || plan.DecisionSetHash != operation.RetentionDecisionSetHash
            || plan.AdmittedOperationRevision != operation.Revision)
            return Result(PurgeAdmissionStatus.Blocked, request, operation.Revision);
        if (!organisation.BeginPurge(beforeWrite))
            return Result(PurgeAdmissionStatus.Conflict, request, operation.Revision);
        var boundaryHash = plan.BoundaryEvidenceHash(beforeWrite);
        operation.BeginPurge(beforeWrite, plan.DecisionSetHash, plan.PlanHash, boundaryHash);
        database.LifecyclePurgePlans.Add(plan);
        var startId = PurgeMessageV1.StartId(plan.Id);
        database.LifecyclePurgeOutbox.Add(LifecyclePurgeOutboxMessage.From(new PurgeMessageV1(
            startId, PurgeMessageKind.IrreversibleStarted, operation.OrganisationId,
            operation.Id, operation.Revision, operation.RegistryRevision, plan.Id,
            plan.PlanHash, plan.DecisionSetId, plan.DecisionSetHash, "", "organisation.purge-started",
            "", "", startId)));
        foreach (var entry in plan.Entries.Where(x => x.Decision == RetentionDecisionCode.Purge))
        {
            var messageId = PurgeMessageV1.CommandId(plan.Id, entry.ParticipantId,
                entry.Category, entry.ItemId);
            var command = new PurgeMessageV1(messageId, PurgeMessageKind.ParticipantPurge,
                operation.OrganisationId, operation.Id, operation.Revision,
                operation.RegistryRevision, plan.Id, plan.PlanHash, plan.DecisionSetId,
                plan.DecisionSetHash, entry.ParticipantId,
                ReviewedDispositionRegistryV1.CapabilityKey, entry.Category, entry.ItemId,
                messageId);
            database.LifecyclePurgeOutbox.Add(LifecyclePurgeOutboxMessage.From(command));
            database.LifecyclePurgeProgress.Add(
                LifecyclePurgeParticipantProgress.Pending(plan, entry, command));
        }
        await database.SaveChangesAsync(cancellationToken);
        return new PurgeAdmissionResult(PurgeAdmissionStatus.Admitted, operation.Id,
            operation.Revision, plan.Id, plan.PlanHash);
    }

    private static PurgeAdmissionResult Result(PurgeAdmissionStatus status,
        PurgeAdmissionRequest request, long? revision = null) =>
        new(status, request.OperationId, revision ?? request.ExpectedOperationRevision);

    private static bool Sha256(string value) => value?.Length == 64
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async Task EstablishContext(AuthDbContext database,
        IDbContextTransaction transaction, Guid organisationId, CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter(); parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D"); command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
            throw new InvalidOperationException("Lifecycle context initialization failed.");
        database.LifecycleOrganisationId = organisationId;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }

    private static void ResetContext(AuthDbContext database)
    {
        database.LifecycleTransaction = null;
        database.LifecycleOrganisationId = Guid.Empty;
    }
}
