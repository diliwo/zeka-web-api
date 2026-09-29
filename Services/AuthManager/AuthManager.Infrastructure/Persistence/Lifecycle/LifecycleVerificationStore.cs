using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

/// <summary>Fixture-only coordinator persistence. It never reads an owner-local payload store.</summary>
public sealed class LifecycleVerificationStore(DbContextOptions<AuthDbContext> options,
    TimeProvider clock, PurgeFixtureScope fixtureScope,
    INonProductionFixtureWriteFence fixtureWriteFence,
    IReadOnlyCollection<INonProductionPurgeVerifier>? trustedOwnerVerifiers = null)
    : ILifecycleVerificationStore
{
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<VerifyPurgeCommandV1>> IssueCommandsAsync(Guid operationId,
        Guid organisationId, CancellationToken cancellationToken)
    {
        fixtureScope.Demand(organisationId);
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SetContext(database, transaction, organisationId, cancellationToken);
            var operation = await database.Set<LifecycleOperation>().AsNoTracking()
                .Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
            var plan = await database.LifecyclePurgePlans.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
            var progress = await database.LifecyclePurgeProgress.AsNoTracking()
                .Where(x => x.OperationId == operationId).ToArrayAsync(cancellationToken);
            var organisation = await database.Organisations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == organisationId, cancellationToken);
            if (!FrozenState(operation, plan, organisation, progress))
                return [];
            var at = clock.GetUtcNow();
            var commands = plan!.Entries.Select(entry => new VerifyPurgeCommandV1(
                Guid.NewGuid(), organisationId, operationId, operation!.IrreversibleRevision!.Value,
                plan.RegistryRevision, plan.InventoryHash, plan.DecisionSetId,
                plan.DecisionSetHash, plan.Id, plan.PlanHash, entry.ParticipantId,
                ReviewedDispositionRegistryV1.CapabilityKey, entry.Category, entry.ItemId,
                entry.Decision == RetentionDecisionCode.Purge ? "PURGE" : "RETAIN",
                at, at.Add(ObservationWindow))).ToArray();
            foreach (var command in commands)
                database.LifecycleVerificationCommands.Add(LifecycleVerificationCommand.Create(
                    command.MessageId, command.TerminationOperationId, command.OrganisationId,
                    command.PlanId, command.ParticipantId, command.Category, command.ItemId,
                    command.Hash(), command.RequestedAt, command.ExpiresAt));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return commands;
        }
        finally { ResetContext(database); }
    }

    public async Task<VerificationResult> RecordObservedAsync(VerifyPurgeCommandV1 command,
        INonProductionPurgeVerifier authenticatedOwner,
        CancellationToken cancellationToken)
    {
        fixtureScope.Demand(command.OrganisationId);
        if (trustedOwnerVerifiers is null
            || trustedOwnerVerifiers.Count(x => ReferenceEquals(x, authenticatedOwner)
                && x.ParticipantId == command.ParticipantId) != 1)
            return new VerificationResult(VerificationStatus.Conflict,
                command.TerminationOperationId, command.Category);
        try { command.Validate(); }
        catch (InvalidOperationException)
        {
            return new VerificationResult(VerificationStatus.Conflict,
                command.TerminationOperationId, command.Category);
        }
        // A previously recorded exact challenge is an idempotent replay only. It does not
        // refresh ObservedAt or satisfy a newer/fresh finalization challenge.
        await using (var replayDatabase = new AuthDbContext(options) { LifecycleOnly = true })
        await using (var replayTransaction = await replayDatabase.Database.BeginTransactionAsync(
                         IsolationLevel.ReadCommitted, cancellationToken))
        {
            try
            {
                await SetContext(replayDatabase, replayTransaction, command.OrganisationId,
                    cancellationToken);
                var previous = await replayDatabase.LifecycleVerificationEvidence.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.OperationId == command.TerminationOperationId
                        && x.Category == command.Category, cancellationToken);
                if (previous?.CommandMessageId == command.MessageId
                    && previous.CommandHash == command.Hash()
                    && previous.ParticipantId == command.ParticipantId)
                    return new VerificationResult(VerificationStatus.Replay,
                        command.TerminationOperationId, command.Category);
            }
            finally { ResetContext(replayDatabase); }
        }
        VerifyPurgeReceiptV1 receipt;
        try { receipt = await authenticatedOwner.ObserveAsync(command, cancellationToken); }
        catch (InvalidOperationException)
        {
            return new VerificationResult(VerificationStatus.Conflict,
                command.TerminationOperationId, command.Category);
        }
        return await RecordValidatedAsync(command, receipt, authenticatedOwner.ParticipantId,
            cancellationToken);
    }

    private async Task<VerificationResult> RecordValidatedAsync(VerifyPurgeCommandV1 command,
        VerifyPurgeReceiptV1 receipt, string authenticatedParticipantId,
        CancellationToken cancellationToken)
    {
        fixtureScope.Demand(command.OrganisationId);
        var result = new VerificationResult(VerificationStatus.Conflict,
            command.TerminationOperationId, command.Category);
        if (authenticatedParticipantId != command.ParticipantId)
            return result;
        try { command.Validate(); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        { return result; }
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SetContext(database, transaction, command.OrganisationId, cancellationToken);
            var issued = await database.LifecycleVerificationCommands.AsNoTracking()
                .SingleOrDefaultAsync(x => x.MessageId == command.MessageId, cancellationToken);
            var existing = await database.LifecycleVerificationEvidence.SingleOrDefaultAsync(
                x => x.OperationId == command.TerminationOperationId
                    && x.Category == command.Category, cancellationToken);
            if (issued is not null && issued.CommandHash == command.Hash()
                && issued.OperationId == command.TerminationOperationId
                && issued.OrganisationId == command.OrganisationId
                && issued.PlanId == command.PlanId
                && issued.ParticipantId == command.ParticipantId
                && issued.Category == command.Category && issued.ItemId == command.ItemId
                && existing is not null && existing.OrganisationId == command.OrganisationId
                && existing.PlanId == command.PlanId
                && existing.CommandMessageId == command.MessageId
                && existing.CommandHash == command.Hash()
                && existing.ReceiptId == receipt.ReceiptId
                && existing.ReceiptHash == receipt.Hash())
                return new VerificationResult(VerificationStatus.Replay,
                    command.TerminationOperationId, command.Category);
            try { receipt.ValidateAgainst(command, clock.GetUtcNow()); }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            { return result; }
            var operation = await database.Set<LifecycleOperation>().AsNoTracking()
                .Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.Id == command.TerminationOperationId, cancellationToken);
            var plan = await database.LifecyclePurgePlans.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == command.PlanId, cancellationToken);
            var progress = await database.LifecyclePurgeProgress.AsNoTracking()
                .Where(x => x.OperationId == command.TerminationOperationId)
                .ToArrayAsync(cancellationToken);
            var organisation = await database.Organisations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == command.OrganisationId, cancellationToken);
            if (!FrozenState(operation, plan, organisation, progress)
                || issued is null || issued.CommandHash != command.Hash()
                || issued.OperationId != command.TerminationOperationId
                || issued.OrganisationId != command.OrganisationId
                || issued.PlanId != command.PlanId || issued.Category != command.Category
                || issued.ParticipantId != command.ParticipantId || issued.ItemId != command.ItemId
                || issued.IssuedAt != command.RequestedAt || issued.ExpiresAt != command.ExpiresAt
                || plan!.Entries.Count(x => x.Category == command.Category
                    && x.ParticipantId == command.ParticipantId && x.ItemId == command.ItemId
                    && command.ExpectedDisposition == (x.Decision == RetentionDecisionCode.Purge
                        ? "PURGE" : "RETAIN")) != 1
                || receipt.VerifierVersion != "synthetic-pg-owner-query-v1"
                || receipt.RetainedExpectedCount != (command.ExpectedDisposition == "RETAIN" ? 1 : 0)
                || receipt.FileExpectedCount != (command.Category == "admin-area-document-storage"
                    && command.ParticipantId == "admin-area-documents"
                    ? command.ExpectedDisposition == "RETAIN" ? 1 : 0 : null)
                || command.IrreversibleRevision != operation!.IrreversibleRevision
                || command.RegistryRevision != plan.RegistryRevision
                || command.InventoryHash != plan.InventoryHash
                || command.DecisionSetId != plan.DecisionSetId
                || command.DecisionSetHash != plan.DecisionSetHash
                || command.PlanHash != plan.PlanHash)
                return result;
            var next = LifecycleVerificationEvidence.Create(new LifecycleVerificationObservation(
                command.TerminationOperationId, command.OrganisationId, command.PlanId,
                command.Category, command.ParticipantId, command.MessageId, command.Hash(),
                receipt.ReceiptId, receipt.Hash(), receipt.EvidenceHash,
                receipt.VerifierVersion, receipt.ObservedAt, receipt.ExpiresAt,
                receipt.EligibleResidualCount, receipt.RetainedPresentCount,
                receipt.RetainedExpectedCount, receipt.FileResidualCount,
                receipt.PostconditionSatisfied));
            if (existing is not null)
            {
                if (existing.IsExactReplay(next))
                    return new VerificationResult(VerificationStatus.Replay,
                        command.TerminationOperationId, command.Category);
                try { existing.ReplaceWithFresh(next); }
                catch (InvalidOperationException) { return result; }
            }
            else database.LifecycleVerificationEvidence.Add(next);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VerificationResult(VerificationStatus.Recorded,
                command.TerminationOperationId, command.Category);
        }
        finally { ResetContext(database); }
    }

    public async Task<VerificationResult> FinalizeAsync(Guid operationId, Guid organisationId,
        long expectedOperationRevision, CancellationToken cancellationToken,
        INonProductionFixtureWriteFenceLease? heldFixtureFence = null)
    {
        fixtureScope.Demand(organisationId);
        if (heldFixtureFence is null)
            throw new InvalidOperationException("Synthetic finalization requires the held owner write fence.");
        fixtureWriteFence.DemandHeld(heldFixtureFence, organisationId, operationId);
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        try
        {
            await SetContext(database, transaction, organisationId, cancellationToken);
            var operation = await database.Set<LifecycleOperation>().Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
            var organisation = await database.Organisations.SingleOrDefaultAsync(
                x => x.Id == organisationId, cancellationToken);
            var plan = await database.LifecyclePurgePlans.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OperationId == operationId, cancellationToken);
            var progress = await database.LifecyclePurgeProgress.AsNoTracking()
                .Where(x => x.OperationId == operationId).ToArrayAsync(cancellationToken);
            if (!FrozenState(operation, plan, organisation, progress)
                || operation!.Revision != expectedOperationRevision)
                return new VerificationResult(VerificationStatus.Conflict, operationId);
            var evidence = await database.LifecycleVerificationEvidence.AsNoTracking()
                .Where(x => x.OperationId == operationId).ToArrayAsync(cancellationToken);
            var issued = await database.LifecycleVerificationCommands.AsNoTracking()
                .Where(x => x.OperationId == operationId).ToArrayAsync(cancellationToken);
            var now = clock.GetUtcNow();
            var entries = plan!.Entries;
            if (evidence.Length != entries.Count || entries.Any(entry =>
                    evidence.Count(x => x.Category == entry.Category
                        && x.ParticipantId == entry.ParticipantId
                        && x.PlanId == plan.Id && x.PostconditionSatisfied
                        && x.ObservedAt <= now && x.ObservedAt >= now.Subtract(ObservationWindow)
                        && x.ExpiresAt > now && x.VerifierVersion == "synthetic-pg-owner-query-v1"
                        && issued.Any(c => c.MessageId == x.CommandMessageId
                            && c.CommandHash == x.CommandHash
                            && c.Category == entry.Category && c.ParticipantId == entry.ParticipantId
                            && c.ItemId == entry.ItemId && c.PlanId == plan.Id
                            && c.IssuedAt >= now.Subtract(ObservationWindow)
                            && !issued.Any(later => later.Category == c.Category
                                && later.OperationId == c.OperationId
                                && later.IssuedAt >= c.IssuedAt
                                && later.MessageId != c.MessageId))) != 1))
                return new VerificationResult(VerificationStatus.Incomplete, operationId);
            // The evidence digest is independent of the LIFE-04A execution receipt hashes.
            var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                "zeka-independent-verification-v1\n" + string.Join("\n", evidence
                    .OrderBy(x => x.Category, StringComparer.Ordinal)
                    .Select(x => x.ReceiptHash))))).ToLowerInvariant();
            operation.MarkVerifiedPurged(now, evidenceHash);
            if (!organisation!.MarkVerifiedPurged(now))
                return new VerificationResult(VerificationStatus.Conflict, operationId);
            var fact = new OrganisationVerifiedPurgedV1(organisationId, operationId,
                operation.Revision, plan.Id, plan.PlanHash, evidenceHash, now);
            database.OutboxMessages.Add(OutboxMessage.Create("OrganisationVerifiedPurgedV1", 1,
                JsonSerializer.Serialize(fact), now, now, operationId.ToString("D"), organisationId));
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VerificationResult(VerificationStatus.Finalized, operationId);
        }
        finally { ResetContext(database); }
    }

    private static bool FrozenState(LifecycleOperation? operation, LifecyclePurgePlan? plan,
        Organisation? organisation, IReadOnlyList<LifecyclePurgeParticipantProgress> progress)
    {
        if (operation is null || plan is null || organisation is null
            || operation.Family != LifecycleOperationFamily.Termination
            || operation.State != LifecycleOperationState.PurgeExecutionComplete
            || organisation.Status != OrganisationStatus.PurgeInProgress
            || !operation.IsActive || operation.CompletedAt is not null
            || operation.IrreversibleRevision is null || operation.IrreversibleStartedAt is null
            || operation.PurgeExecutionCompletedAt is null || operation.PurgeBoundaryEvidenceHash is null
            || operation.OrganisationId != plan.OrganisationId || organisation.Id != plan.OrganisationId
            || operation.Id != plan.OperationId || operation.RegistryRevision != plan.RegistryRevision
            || operation.InventoryHash != plan.InventoryHash
            || operation.DispositionInventoryHash != plan.DispositionInventoryHash
            || operation.RetentionDecisionSetHash != plan.DecisionSetHash
            || operation.PurgePlanHash != plan.PlanHash
            || !plan.HasValidFrozenIdentity()
            || operation.IrreversibleRevision != plan.AdmittedOperationRevision + 1
            || !ReviewedDispositionRegistryV1.HasCompleteFrozenInventory(operation.Participants)
            || ReviewedDispositionRegistryV1.FrozenCategoryHash(operation.Participants)
                != operation.DispositionInventoryHash
            || plan.Entries.Count != operation.Participants.Count(x =>
                x.CapabilityKey == ReviewedDispositionRegistryV1.CapabilityKey)
            || plan.Entries.Any(entry => entry.ItemId != $"fixture:{entry.Category}"
                || entry.Decision is not (RetentionDecisionCode.Purge or RetentionDecisionCode.Retain)
                || operation.Participants.Count(participant =>
                    participant.Family == LifecycleOperationFamily.Termination
                    && participant.CapabilityKey == ReviewedDispositionRegistryV1.CapabilityKey
                    && participant.OwnershipScope == entry.Category
                    && participant.ParticipantId == entry.ParticipantId
                    && participant.ContractVersion == entry.ContractVersion) != 1)
            || plan.Entries.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count()
                != plan.Entries.Count
            || progress.Count != plan.Entries.Count(x => x.Decision == RetentionDecisionCode.Purge))
            return false;
        return plan.Entries.Where(x => x.Decision == RetentionDecisionCode.Purge).All(entry =>
            progress.Count(x => x.OperationId == operation.Id && x.PlanId == plan.Id
                && x.OrganisationId == operation.OrganisationId && x.Category == entry.Category
                && x.ParticipantId == entry.ParticipantId && x.ItemId == entry.ItemId
                && x.CommandMessageId == PurgeMessageV1.CommandId(plan.Id,
                    entry.ParticipantId, entry.Category, entry.ItemId)
                && x.State is PurgeProgressState.Purged or PurgeProgressState.AlreadyAbsent) == 1);
    }

    private static async Task SetContext(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisationId, CancellationToken cancellationToken)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
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
