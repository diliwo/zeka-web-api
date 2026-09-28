using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Persistence.Lifecycle;

public sealed class LifecycleExportStore(
    DbContextOptions<AuthDbContext> options,
    IExportPackageAssembler assembler,
    IExportArtifactSource artifacts,
    IExportPackageSink packages,
    IReviewedExportCategoryInventory categoryInventory,
    TimeProvider clock) : ILifecycleExportStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<ExportProgressResult> BeginAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken) => ExecuteAsync(operationId, organisationId, async database =>
    {
        var operation = await Operation(database, operationId, cancellationToken);
        if (operation is null || operation.Family != LifecycleOperationFamily.Export)
            return Result(ExportProgressStatus.Rejected, operationId, operation?.Revision ?? 0);
        if (operation.State != LifecycleOperationState.Requested)
            return Result(ExportProgressStatus.Replay, operationId, operation.Revision);

        operation.BeginExport();
        foreach (var participant in FenceParticipants(operation))
        {
            var header = Header(operation, participant, Guid.NewGuid(), operation.Id);
            Enqueue(database, operation, nameof(EnterOrganisationExportFenceV1),
                new EnterOrganisationExportFenceV1(header));
        }
        await database.SaveChangesAsync(cancellationToken);
        return Result(ExportProgressStatus.Progressed, operationId, operation.Revision);
    }, cancellationToken);

    public Task<ExportProgressResult> AcceptFenceAsync(OrganisationExportFenceEnteredV1 receipt,
        CancellationToken cancellationToken) => ExecuteAsync(receipt.Header.OperationId,
        receipt.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, receipt.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ExportProgressStatus.Rejected, receipt.Header.OperationId, 0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(receipt, Json);
        var dedupe = await Dedupe(database, receipt.Header, nameof(OrganisationExportFenceEnteredV1), payload, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision);
        if (operation.State != LifecycleOperationState.EnteringFence
            || receipt.Header.OperationRevision != operation.Revision
            || !Matches(operation, receipt.Header, OrganisationExportCapabilityV1.Fence))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);

        operation.RecordFenceAccepted(receipt.Header.ParticipantId);
        database.Add(LifecycleExportFenceReceipt.Create(operation.Id, operation.OrganisationId,
            receipt.Header.ParticipantId, receipt.Header.ContractVersion, receipt.Header.OperationRevision,
            receipt.FenceToken, receipt.FenceRevision, receipt.EnteredAt, receipt.ReceiptHash,
            receipt.Header.MessageId, receipt.Header.CausationId, receipt.Header.CorrelationId));

        if (FenceParticipants(operation).All(x => !x.Mandatory || x.State == LifecycleParticipantState.Accepted))
        {
            var requirements = FenceParticipants(operation).Where(x => x.Mandatory)
                .Select(x => new ExportFenceParticipantRequirementV1(x.ParticipantId, x.ContractVersion));
            var persisted = await database.LifecycleExportFenceReceipts.Where(x => x.OperationId == operation.Id)
                .OrderBy(x => x.ParticipantId).ToListAsync(cancellationToken);
            var receipts = persisted.Select(ToContract).Append(new ExportFenceReceiptV1(receipt)).ToArray();
            var evidence = new CompleteExportFenceEvidenceV1(operation.RegistryRevision,
                operation.InventoryHash, requirements, receipts);
            var snapshotAt = clock.GetUtcNow();
            operation.EstablishSnapshot(snapshotAt, evidence.EvidenceHash);

            foreach (var participant in FragmentParticipants(operation))
            {
                var fenceOwner = ResolveFenceOwner(operation, participant);
                var header = Header(operation, participant, Guid.NewGuid(), receipt.Header.MessageId);
                var payloadModel = new
                {
                    Header = header,
                    SnapshotAt = snapshotAt,
                    FenceEvidence = evidence,
                    FenceOwnerParticipantId = fenceOwner.ParticipantId
                };
                Enqueue(database, operation, nameof(StageOrganisationExportV1), payloadModel);
            }
        }
        await database.SaveChangesAsync(cancellationToken);
        return Result(operation.State == LifecycleOperationState.StagingFragments
            ? ExportProgressStatus.Progressed : ExportProgressStatus.AwaitingParticipants,
            operation.Id, operation.Revision);
    }, cancellationToken);

    public Task<ExportProgressResult> AcceptFragmentAsync(OrganisationExportFragmentReadyV1 fragment,
        CancellationToken cancellationToken) => ExecuteAsync(fragment.Header.OperationId,
        fragment.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, fragment.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ExportProgressStatus.Rejected, fragment.Header.OperationId, 0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(fragment, Json);
        var dedupe = await Dedupe(database, fragment.Header, nameof(OrganisationExportFragmentReadyV1), payload, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision);
        if (operation.State != LifecycleOperationState.StagingFragments
            || operation.SnapshotAt != fragment.SnapshotAt
            || fragment.Header.OperationRevision != operation.Revision
            || !MatchesFragment(operation, fragment.Header))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);
        var requirements = categoryInventory.RequirementsFor(fragment.Header.ParticipantId)
            .OrderBy(x => x.Category, StringComparer.Ordinal).ToArray();
        var observed = fragment.Categories.OrderBy(x => x.Category, StringComparer.Ordinal).ToArray();
        if (requirements.Length != observed.Length || requirements.Zip(observed).Any(pair =>
                pair.First.Category != pair.Second.Category
                || !pair.First.AllowedDispositions.Contains(pair.Second.Disposition)))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);
        var fenceOwner = ResolveFenceOwner(operation,
            FragmentParticipants(operation).Single(x => x.ParticipantId == fragment.Header.ParticipantId));
        var fence = await database.LifecycleExportFenceReceipts.SingleAsync(x =>
            x.OperationId == operation.Id && x.ParticipantId == fenceOwner.ParticipantId, cancellationToken);
        if (!string.Equals(fence.FenceToken, fragment.FenceToken, StringComparison.Ordinal))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);

        var categoriesJson = JsonSerializer.Serialize(fragment.Categories, Json);
        database.Add(LifecycleExportFragment.Create(operation.Id, operation.OrganisationId,
            fragment.Header.ParticipantId, fragment.Header.ContractVersion, fragment.Header.OperationRevision,
            fragment.SnapshotAt, fragment.FenceToken, fragment.FragmentHash, categoriesJson, clock.GetUtcNow()));
        operation.RecordFragmentAccepted(fragment.Header.ParticipantId);
        await database.SaveChangesAsync(cancellationToken);
        if (operation.State != LifecycleOperationState.AssemblingPackage)
            return Result(ExportProgressStatus.AwaitingParticipants, operation.Id, operation.Revision);
        return await AssembleAsync(database, operation, cancellationToken);
    }, cancellationToken);

    public Task<ExportProgressResult> AcceptReleaseAsync(OrganisationExportFenceReleasedV1 release,
        CancellationToken cancellationToken) => ExecuteAsync(release.Header.OperationId,
        release.Header.OrganisationId, async database =>
    {
        var operation = await Operation(database, release.Header.OperationId, cancellationToken);
        if (operation is null) return Result(ExportProgressStatus.Rejected, release.Header.OperationId, 0);
        var payload = JsonSerializer.SerializeToUtf8Bytes(release, Json);
        var dedupe = await Dedupe(database, release.Header, nameof(OrganisationExportFenceReleasedV1), payload, cancellationToken);
        if (dedupe is not null) return Result(dedupe.Value, operation.Id, operation.Revision,
            operation.PackageSha256, operation.PackageReference);
        if (operation.State != LifecycleOperationState.ReleasingFence
            || release.Header.OperationRevision != operation.Revision
            || !Matches(operation, release.Header, OrganisationExportCapabilityV1.Fence))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);
        var fence = await database.LifecycleExportFenceReceipts.SingleAsync(x =>
            x.OperationId == operation.Id && x.ParticipantId == release.Header.ParticipantId, cancellationToken);
        if (!string.Equals(fence.FenceToken, release.FenceToken, StringComparison.Ordinal))
            return Result(ExportProgressStatus.Conflict, operation.Id, operation.Revision);
        operation.RecordFenceReleased(release.Header.ParticipantId);
        await database.SaveChangesAsync(cancellationToken);
        return Result(operation.State == LifecycleOperationState.Completed
                ? ExportProgressStatus.Completed : ExportProgressStatus.AwaitingParticipants,
            operation.Id, operation.Revision, operation.PackageSha256, operation.PackageReference);
    }, cancellationToken);

    public async Task<IReadOnlyList<Guid>> RecoverableAsync(Guid organisationId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var database = new AuthDbContext(options) { LifecycleOnly = true };
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await InitializeAsync(database, transaction, organisationId, cancellationToken);
            return await database.Set<LifecycleOperation>().Where(x => x.IsActive
                    && x.Family == LifecycleOperationFamily.Export)
                .OrderBy(x => x.RequestedAt).Select(x => x.Id).ToListAsync(cancellationToken);
        }
        finally { Clear(database); }
    }

    public Task<ExportProgressResult> ResumeAsync(Guid operationId, Guid organisationId,
        CancellationToken cancellationToken) => ExecuteAsync(operationId, organisationId, async database =>
    {
        var operation = await Operation(database, operationId, cancellationToken);
        if (operation is null) return Result(ExportProgressStatus.Rejected, operationId, 0);
        if (operation.State == LifecycleOperationState.Requested)
        {
            operation.BeginExport();
            foreach (var participant in FenceParticipants(operation))
            {
                var header = Header(operation, participant, Guid.NewGuid(), operation.Id);
                Enqueue(database, operation, nameof(EnterOrganisationExportFenceV1),
                    new EnterOrganisationExportFenceV1(header));
            }
            await database.SaveChangesAsync(cancellationToken);
            return Result(ExportProgressStatus.Progressed, operation.Id, operation.Revision);
        }
        if (operation.State == LifecycleOperationState.AssemblingPackage)
            return await AssembleAsync(database, operation, cancellationToken);
        return Result(ExportProgressStatus.AwaitingParticipants, operation.Id, operation.Revision,
            operation.PackageSha256, operation.PackageReference);
    }, cancellationToken);

    private async Task<ExportProgressResult> AssembleAsync(AuthDbContext database, LifecycleOperation operation,
        CancellationToken cancellationToken)
    {
        var existing = await database.LifecycleExportPackages.SingleOrDefaultAsync(
            x => x.OperationId == operation.Id, cancellationToken);
        if (existing is not null)
            return Result(ExportProgressStatus.Progressed, operation.Id, operation.Revision,
                existing.PackageSha256, existing.PackageReference);

        var now = clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var lease = await database.LifecycleCoordinatorLeases.SingleOrDefaultAsync(
            x => x.OperationId == operation.Id, cancellationToken);
        if (lease is null)
        {
            lease = LifecycleCoordinatorLease.Create(operation.Id, operation.OrganisationId,
                leaseId, now.AddMinutes(5));
            database.Add(lease);
        }
        else if (!lease.TryAcquire(leaseId, now, now.AddMinutes(5)))
            return Result(ExportProgressStatus.AwaitingParticipants, operation.Id, operation.Revision);

        var fragments = await database.LifecycleExportFragments.Where(x => x.OperationId == operation.Id)
            .OrderBy(x => x.ParticipantId).ToListAsync(cancellationToken);
        var categories = fragments.SelectMany(fragment =>
            JsonSerializer.Deserialize<ExportCategoryFragmentV1[]>(fragment.CategoriesJson, Json)!
                .Select(category => new ExportPackageCategory(fragment.ParticipantId, category.Category,
                    category.DispositionCode, category.RecordCount, category.SchemaVersion, category.ContentSha256,
                    category.ArtifactReference, category.ReasonCode))).ToArray();
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var reference in categories.Where(x => x.ArtifactReference is not null)
                     .Select(x => x.ArtifactReference!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            bytes.Add(reference, (await artifacts.ReadAsync(reference, cancellationToken)).ToArray());

        var output = assembler.Assemble(new ExportPackageInput(operation.Id, operation.OrganisationId,
            operation.RegistryRevision, operation.InventoryHash, operation.SnapshotAt!.Value,
            operation.FenceEvidenceHash!, categories, bytes));
        var packageReference = await packages.StoreAsync(operation.Id, output.PackageSha256,
            output.Content, cancellationToken);
        database.Add(LifecycleExportPackage.Create(operation.Id, operation.OrganisationId,
            output.ManifestSha256, output.PackageSha256, packageReference, now));
        operation.RecordPackage(output.PackageSha256, packageReference, now);
        foreach (var participant in FenceParticipants(operation))
        {
            var receipt = await database.LifecycleExportFenceReceipts.SingleAsync(x =>
                x.OperationId == operation.Id && x.ParticipantId == participant.ParticipantId, cancellationToken);
            var header = Header(operation, participant, Guid.NewGuid(), operation.Id);
            Enqueue(database, operation, nameof(ReleaseOrganisationExportFenceV1),
                new ReleaseOrganisationExportFenceV1(header, receipt.FenceToken));
        }
        await database.SaveChangesAsync(cancellationToken);
        return Result(ExportProgressStatus.Progressed, operation.Id, operation.Revision,
            output.PackageSha256, packageReference);
    }

    private async Task<ExportProgressStatus?> Dedupe(AuthDbContext database, LifecycleMessageHeaderV1 header,
        string messageType, byte[] payload, CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var prior = await database.LifecycleInboxReceipts.SingleOrDefaultAsync(x =>
            x.MessageId == header.MessageId, cancellationToken);
        if (prior is not null)
            return prior.OperationId == header.OperationId && prior.OrganisationId == header.OrganisationId
                && prior.MessageType == messageType && prior.PayloadSha256 == hash
                ? ExportProgressStatus.Replay : ExportProgressStatus.Conflict;
        database.Add(LifecycleInboxReceipt.Create(header.MessageId, header.OperationId,
            header.OrganisationId, messageType, hash, clock.GetUtcNow()));
        return null;
    }

    private async Task<ExportProgressResult> ExecuteAsync(Guid operationId, Guid organisationId,
        Func<AuthDbContext, Task<ExportProgressResult>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await database.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);
                    try
                    {
                        await InitializeAsync(database, transaction, organisationId, cancellationToken);
                        var result = await action(database);
                        await transaction.CommitAsync(cancellationToken);
                        return result;
                    }
                    finally { Clear(database); }
                });
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure }) { }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { }
        }
        return Result(ExportProgressStatus.Unavailable, operationId, 0);
    }

    private static async Task<LifecycleOperation?> Operation(AuthDbContext database, Guid operationId,
        CancellationToken cancellationToken) => await database.Set<LifecycleOperation>()
        .Include(x => x.Participants).SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken);

    private static LifecycleParticipant[] FenceParticipants(LifecycleOperation operation) =>
        operation.Participants.Where(x => x.Family == LifecycleOperationFamily.Export
            && x.CapabilityKey == OrganisationExportCapabilityV1.Fence)
            .OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();

    private static LifecycleParticipant[] FragmentParticipants(LifecycleOperation operation) =>
        operation.Participants.Where(x => x.Family == LifecycleOperationFamily.Export
            && x.CapabilityKey.EndsWith(".export-fragment", StringComparison.Ordinal))
            .OrderBy(x => x.ParticipantId, StringComparer.Ordinal).ToArray();

    private static LifecycleParticipant ResolveFenceOwner(LifecycleOperation operation,
        LifecycleParticipant fragment) => FenceParticipants(operation).SingleOrDefault(x =>
            x.ParticipantId == fragment.ParticipantId || x.OwnershipScope == fragment.OwnershipScope)
        ?? throw new InvalidOperationException("Every fragment capability must resolve to exactly one frozen fence owner.");

    private static bool Matches(LifecycleOperation operation, LifecycleMessageHeaderV1 header, string capability) =>
        operation.Id == header.OperationId && operation.OrganisationId == header.OrganisationId
        && operation.Participants.Any(x => x.Family == LifecycleOperationFamily.Export
            && x.CapabilityKey == capability && x.ParticipantId == header.ParticipantId
            && x.ContractVersion == header.ContractVersion);

    private static bool MatchesFragment(LifecycleOperation operation, LifecycleMessageHeaderV1 header) =>
        operation.Id == header.OperationId && operation.OrganisationId == header.OrganisationId
        && operation.Participants.Any(x => x.Family == LifecycleOperationFamily.Export
            && x.CapabilityKey.EndsWith(".export-fragment", StringComparison.Ordinal)
            && x.ParticipantId == header.ParticipantId && x.ContractVersion == header.ContractVersion);

    private static LifecycleMessageHeaderV1 Header(LifecycleOperation operation,
        LifecycleParticipant participant, Guid messageId, Guid causationId) => new(
        operation.Id, operation.OrganisationId, operation.Revision, participant.ParticipantId,
        participant.ContractVersion, messageId, causationId, operation.Id);

    private static ExportFenceReceiptV1 ToContract(LifecycleExportFenceReceipt receipt) => new(
        new LifecycleMessageHeaderV1(receipt.OperationId, receipt.OrganisationId, receipt.OperationRevision,
            receipt.ParticipantId, receipt.ContractVersion, receipt.MessageId,
            receipt.CausationId, receipt.CorrelationId),
        receipt.FenceToken, receipt.FenceRevision, receipt.EnteredAt, receipt.ReceiptHash);

    private void Enqueue(AuthDbContext database, LifecycleOperation operation,
        string messageType, object payload)
    {
        var now = clock.GetUtcNow();
        database.OutboxMessages.Add(OutboxMessage.Create(messageType, LifecycleContractV1.Version,
            JsonSerializer.Serialize(payload, Json), now, now, operation.Id.ToString("D"), operation.OrganisationId));
    }

    private static ExportProgressResult Result(ExportProgressStatus status, Guid operationId, long revision,
        string? packageHash = null, string? packageReference = null) =>
        new(status, operationId, revision, packageHash, packageReference);

    private static async Task InitializeAsync(AuthDbContext database, IDbContextTransaction transaction,
        Guid organisation, CancellationToken cancellationToken)
    {
        if (organisation == Guid.Empty) throw new InvalidOperationException("Authorized organisation is required.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "select pg_catalog.set_config('zeka.organisation_id', @organisation_id, true)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisation.ToString("D");
        command.Parameters.Add(parameter);
        if (!Equals(await command.ExecuteScalarAsync(cancellationToken), parameter.Value))
            throw new InvalidOperationException("Lifecycle context initialization failed.");
        database.LifecycleOrganisationId = organisation;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }

    private static void Clear(AuthDbContext database)
    {
        database.LifecycleTransaction = null;
        database.LifecycleOrganisationId = Guid.Empty;
    }
}
