using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

/// <summary>Durable eligibility store; deliberately not registered as a production trigger.</summary>
public sealed class LifecycleDispositionStore(DbContextOptions<AuthDbContext> options)
    : ILifecycleDispositionStore
{
    public async Task<RetentionEvaluationRequest?> ReadRequestAsync(Guid evaluationId,
        Guid operationId, Guid organisationId, DateTimeOffset evaluatedAt,
        CancellationToken cancellationToken)
    {
        if (evaluationId == Guid.Empty || operationId == Guid.Empty || organisationId == Guid.Empty
            || evaluatedAt == default || evaluatedAt.Offset != TimeSpan.Zero) return null;
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await EstablishContext(database, transaction, organisationId, cancellationToken);
            var operation = await database.Set<LifecycleOperation>().Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);
            if (!Complete(operation)) return null;
            var existing = await database.RetentionDecisionSets.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == evaluationId, cancellationToken);
            if (existing is not null && existing.OperationId != operationId) return null;
            if (existing is null && await database.RetentionDecisionSets.AsNoTracking()
                    .AnyAsync(x => x.OperationId == operationId, cancellationToken))
                return null; // No implicit policy replacement or hold-release protocol.
            if (existing is null && (operation!.State != LifecycleOperationState.Archived
                || !operation!.IsActive || operation!.ArchivedAt!.Value > evaluatedAt)) return null;
            var categories = Categories(operation!).ToArray();
            return new RetentionEvaluationRequest(evaluationId, organisationId, operationId,
                existing?.OperationRevision ?? operation!.Revision, operation!.RegistryRevision,
                operation.InventoryHash, operation.DispositionInventoryHash!,
                existing?.EvaluatedAt ?? evaluatedAt, categories);
        }
        finally { ResetContext(database); }
    }

    public async Task<DispositionEvaluationResult> CommitAsync(RetentionEvaluationRequest request,
        RetentionEvaluationResponse response, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                await using var transaction = await database.Database.BeginTransactionAsync(
                    IsolationLevel.Serializable, cancellationToken);
                try
                {
                    await EstablishContext(database, transaction, request.OrganisationId, cancellationToken);
                    var result = await CommitInTransaction(database, request, response, cancellationToken);
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
        return new(DispositionEvaluationStatus.Unavailable, request.TerminationOperationId,
            request.OperationRevision);
    }

    private static async Task<DispositionEvaluationResult> CommitInTransaction(AuthDbContext database,
        RetentionEvaluationRequest request, RetentionEvaluationResponse response,
        CancellationToken cancellationToken)
    {
        var operation = await database.Set<LifecycleOperation>().Include(x => x.Participants)
            .SingleOrDefaultAsync(x => x.Id == request.TerminationOperationId, cancellationToken);
        if (!Complete(operation) || operation!.OrganisationId != request.OrganisationId
            || operation.RegistryRevision != request.RegistryRevision
            || operation.InventoryHash != request.InventoryHash
            || operation.DispositionInventoryHash != request.DispositionInventoryHash
            || !Categories(operation).SequenceEqual(request.Categories))
            return Result(DispositionEvaluationStatus.Conflict, request);

        if (!Validate(request, response, out var policyId, out var policyVersion))
            return Result(DispositionEvaluationStatus.Blocked, request);
        var setHash = Hash(request, response, policyId!, policyVersion!);
        var prior = await database.RetentionDecisionSets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.EvaluationId, cancellationToken);
        if (prior is not null)
            return Partition(new(prior.OperationId == request.TerminationOperationId
                    && prior.OrganisationId == request.OrganisationId
                    && prior.OperationRevision == request.OperationRevision
                    && prior.SetHash == setHash
                    ? DispositionEvaluationStatus.Replay : DispositionEvaluationStatus.Conflict,
                operation.Id, operation.Revision, prior.SetHash), response, request.EvaluatedAt);

        if (await database.RetentionDecisionSets.AsNoTracking()
                .AnyAsync(x => x.OperationId == operation.Id, cancellationToken))
            return Result(DispositionEvaluationStatus.Blocked, request);

        if (operation.State != LifecycleOperationState.Archived || !operation.IsActive
            || operation.Revision != request.OperationRevision || operation.ArchivedAt > request.EvaluatedAt
            || operation.CompletedAt is not null)
            return Result(DispositionEvaluationStatus.Conflict, request);
        var organisation = await database.Organisations.SingleOrDefaultAsync(
            x => x.Id == request.OrganisationId, cancellationToken);
        if (organisation?.Status != OrganisationStatus.Archived)
            return Result(DispositionEvaluationStatus.Conflict, request);

        var ready = response.Decisions.All(x => x.Decision == RetentionDecisionCode.Retain
            || x.Decision == RetentionDecisionCode.Purge && x.EligibleAt <= request.EvaluatedAt);
        var set = RetentionDecisionSet.Create(request.EvaluationId, operation,
            policyId!, policyVersion!, request.EvaluatedAt, setHash, ready);
        database.RetentionDecisionSets.Add(set);
        foreach (var category in request.Categories)
        {
            var decision = response.Decisions.Single(x => x.Category == category.Category);
            database.RetentionDecisionRecords.Add(RetentionDecisionRecord.Create(set,
                category.Category, category.ParticipantId, category.ContractVersion,
                decision.DecidedAt, decision.ValidUntil, decision.Decision,
                decision.EligibleAt, decision.HoldReference, decision.ReasonCode));
        }
        if (ready)
        {
            if (!organisation.MarkDispositionReady(request.EvaluatedAt))
                return Result(DispositionEvaluationStatus.Conflict, request);
            operation.MarkDispositionReady(request.EvaluatedAt, setHash);
        }
        await database.SaveChangesAsync(cancellationToken);
        return Partition(new(ready ? DispositionEvaluationStatus.Ready : DispositionEvaluationStatus.Blocked,
            operation.Id, operation.Revision, setHash), response, request.EvaluatedAt);
    }

    private static DispositionEvaluationResult Partition(DispositionEvaluationResult result,
        RetentionEvaluationResponse response, DateTimeOffset evaluatedAt)
    {
        if (result.Status is DispositionEvaluationStatus.Conflict) return result;
        return result with
        {
            RetainedExceptions = response.Decisions.Where(x => x.Decision == RetentionDecisionCode.Retain)
                .Select(x => x.Category).Order(StringComparer.Ordinal).ToArray(),
            PurgeEligibleCategories = response.Decisions.Where(x => x.Decision == RetentionDecisionCode.Purge
                    && x.EligibleAt <= evaluatedAt)
                .Select(x => x.Category).Order(StringComparer.Ordinal).ToArray(),
            FuturePurgeCategories = response.Decisions.Where(x => x.Decision == RetentionDecisionCode.Purge
                    && x.EligibleAt > evaluatedAt)
                .Select(x => x.Category).Order(StringComparer.Ordinal).ToArray()
        };
    }

    private static bool Complete(LifecycleOperation? operation) => operation is not null
        && operation.Family == LifecycleOperationFamily.Termination
        && operation.RegistryRevision == ReviewedDispositionRegistryV1.Revision
        && operation.DispositionInventoryHash is not null
        && operation.DispositionInventoryHash == ReviewedDispositionRegistryV1.FrozenCategoryHash(operation.Participants)
        && ReviewedDispositionRegistryV1.HasCompleteFrozenInventory(operation.Participants)
        && operation.ArchivedAt is not null && operation.ClosureFenceEvidenceHash is not null;

    private static IEnumerable<DispositionCategory> Categories(LifecycleOperation operation) =>
        operation.Participants.Where(x => x.Family == LifecycleOperationFamily.Termination
                && x.CapabilityKey == ReviewedDispositionRegistryV1.CapabilityKey)
            .OrderBy(x => x.OwnershipScope, StringComparer.Ordinal)
            .Select(x => new DispositionCategory(x.OwnershipScope, x.ParticipantId, x.ContractVersion));

    private static bool Validate(RetentionEvaluationRequest request,
        RetentionEvaluationResponse response, out string? policyId, out string? policyVersion)
    {
        policyId = null; policyVersion = null;
        if (request.EvaluationId == Guid.Empty || request.OperationRevision < 1
            || request.EvaluatedAt == default || request.EvaluatedAt.Offset != TimeSpan.Zero
            || response.Decisions is null || response.Decisions.Count != request.Categories.Count
            || response.Decisions.Count == 0
            || response.Decisions.Select(x => x.Category).Distinct(StringComparer.Ordinal).Count()
                != request.Categories.Count
            || response.Decisions.Any(x => !request.Categories.Any(c => c.Category == x.Category)))
            return false;
        var first = response.Decisions[0];
        policyId = first.PolicyId; policyVersion = first.PolicyVersion;
        if (!Stable(policyId) || !Stable(policyVersion)) return false;
        foreach (var decision in response.Decisions)
        {
            if (decision.PolicyId != policyId || decision.PolicyVersion != policyVersion
                || decision.DecidedAt == default || decision.DecidedAt.Offset != TimeSpan.Zero
                || decision.DecidedAt > request.EvaluatedAt
                || decision.ValidUntil.Offset != TimeSpan.Zero
                || decision.ValidUntil < request.EvaluatedAt
                || !Enum.IsDefined(decision.Decision)
                || !Stable(decision.ReasonCode)
                || (decision.Decision == RetentionDecisionCode.Purge) != (decision.EligibleAt is not null)
                || decision.EligibleAt is { } eligible && eligible.Offset != TimeSpan.Zero
                || decision.Decision == RetentionDecisionCode.Held && !Stable(decision.HoldReference)
                || decision.Decision != RetentionDecisionCode.Held && decision.HoldReference is not null)
                return false;
        }
        return true;
    }

    private static bool Stable(string? value) => !string.IsNullOrWhiteSpace(value)
        && value == value.Trim() && value.Length <= 200;

    private static string Hash(RetentionEvaluationRequest request,
        RetentionEvaluationResponse response, string policyId, string policyVersion)
    {
        var ordered = response.Decisions.OrderBy(x => x.Category, StringComparer.Ordinal)
            .Select(x => new object?[] { x.Category, x.DecidedAt, x.ValidUntil,
                (int)x.Decision, x.EligibleAt, x.HoldReference, x.ReasonCode });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            "zeka-retention-set-v1", request.EvaluationId, request.OrganisationId,
            request.TerminationOperationId, request.OperationRevision, request.RegistryRevision,
            request.InventoryHash, request.DispositionInventoryHash, request.EvaluatedAt,
            policyId, policyVersion, ordered
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static DispositionEvaluationResult Result(DispositionEvaluationStatus status,
        RetentionEvaluationRequest request) => new(status, request.TerminationOperationId,
            request.OperationRevision);

    private static async Task EstablishContext(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisationId, CancellationToken cancellationToken)
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
