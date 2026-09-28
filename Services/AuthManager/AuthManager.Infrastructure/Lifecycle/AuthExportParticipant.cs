using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Lifecycle;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Lifecycle;

/// <summary>
/// AuthManagement-owned durable LIFE-01 participant. Each inbound command, local transition and
/// outbound receipt is committed in one database transaction. Production transport remains absent.
/// </summary>
public sealed class AuthExportParticipant(
    DbContextOptions<AuthDbContext> options,
    IExportArtifactSink artifacts,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrganisationExportFenceEnteredV1> EnterAsync(EnterOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command.Header,
        nameof(EnterOrganisationExportFenceV1), command, async database =>
        {
            await AcquireBarrierLock(database, command.Header.OrganisationId, cancellationToken);
            var existing = await database.AuthExportParticipantExecutions.SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, cancellationToken);
            if (existing is not null)
                throw new InvalidOperationException("A distinct enter command cannot replace durable Auth fence state.");
            var enteredAt = clock.GetUtcNow();
            var token = "auth-" + Hash(Encoding.UTF8.GetBytes(
                $"{command.Header.OperationId:D}\n{command.Header.OrganisationId:D}"))[..32];
            var receipt = new OrganisationExportFenceEnteredV1(
                ReplyHeader(command.Header), token, 1, enteredAt);
            database.Add(AuthExportParticipantExecution.Enter(command.Header.OperationId,
                command.Header.OrganisationId, command.Header.OperationRevision, token,
                enteredAt, receipt.ReceiptHash));
            return receipt;
        }, cancellationToken);

    public Task<OrganisationExportFragmentReadyV1> StageAsync(StageOrganisationExportV1 command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command.Header,
        nameof(StageOrganisationExportV1), command, async database =>
        {
            var execution = await database.AuthExportParticipantExecutions.SingleAsync(
                x => x.OperationId == command.Header.OperationId, cancellationToken);
            var fence = command.FenceEvidence.Receipts.SingleOrDefault(x =>
                x.ParticipantId == AuthExportInventoryV1.ParticipantId)
                ?? throw new InvalidOperationException("Complete fence evidence omits AuthManagement.");
            if (execution.State != AuthExportParticipantState.FenceEntered
                || execution.FenceToken != fence.FenceToken
                || execution.FenceReceiptHash != fence.ReceiptHash
                || command.FenceOwnerParticipantId != AuthExportInventoryV1.ParticipantId)
                throw new InvalidOperationException("Stage command does not match durable Auth fence evidence.");

            var memberships = await database.OrganisationMemberships.AsNoTracking()
                .Where(x => x.OrganisationId == command.Header.OrganisationId && x.JoinedAtUtc <= command.SnapshotAt)
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.UserId, x.PermissionSetId, x.Status, x.JoinedAtUtc,
                    x.SuspendedAtUtc, x.EndedAtUtc })
                .ToListAsync(cancellationToken);

            var csv = new StringBuilder("membership_id,user_id,permission_set_id,status,joined_at_utc,suspended_at_utc,ended_at_utc\n");
            foreach (var row in memberships)
                csv.Append(row.Id.ToString("D")).Append(',').Append(row.UserId.ToString("D")).Append(',')
                    .Append(row.PermissionSetId.ToString("D")).Append(',').Append(row.Status).Append(',')
                    .Append(Format(row.JoinedAtUtc)).Append(',').Append(Format(row.SuspendedAtUtc)).Append(',')
                    .Append(Format(row.EndedAtUtc)).Append('\n');
            var content = Encoding.UTF8.GetBytes(csv.ToString());
            var contentHash = Hash(content);
            var reference = $"fragments/{command.Header.ParticipantId}/{AuthExportInventoryV1.Memberships}.csv";
            await artifacts.StoreAsync(reference, contentHash, content, cancellationToken);
            var fragment = new OrganisationExportFragmentReadyV1(ReplyHeader(command.Header),
                command.SnapshotAt, execution.FenceToken,
            [
                new ExportCategoryFragmentV1(AuthExportInventoryV1.Memberships,
                    memberships.Count == 0 ? ExportCategoryDispositionV1.Empty : ExportCategoryDispositionV1.Included,
                    memberships.Count, "auth-memberships-v1", contentHash, reference, null),
                new ExportCategoryFragmentV1(AuthExportInventoryV1.AuditEvents,
                    ExportCategoryDispositionV1.Withheld, 0, null, null, null, "disclosure-policy-unresolved"),
                new ExportCategoryFragmentV1(AuthExportInventoryV1.IntegrationEvents,
                    ExportCategoryDispositionV1.Withheld, 0, null, null, null, "raw-outbox-export-prohibited")
            ]);
            execution.RecordFragment(command.SnapshotAt, command.FenceEvidenceHash,
                fragment.FragmentHash, JsonSerializer.Serialize(fragment.Categories, Json));
            return fragment;
        }, cancellationToken);

    public Task<OrganisationExportFenceReleasedV1> ReleaseAsync(ReleaseOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default) => ExecuteAsync(command.Header,
        nameof(ReleaseOrganisationExportFenceV1), command, async database =>
        {
            var execution = await database.AuthExportParticipantExecutions.SingleAsync(
                x => x.OperationId == command.Header.OperationId, cancellationToken);
            if (execution.FenceToken != command.FenceToken)
                throw new InvalidOperationException("Release token does not match the durable Auth fence.");
            var releasedAt = clock.GetUtcNow();
            execution.Release(releasedAt);
            return new OrganisationExportFenceReleasedV1(ReplyHeader(command.Header), command.FenceToken, releasedAt);
        }, cancellationToken);

    private async Task<T> ExecuteAsync<T>(LifecycleMessageHeaderV1 header, string messageType,
        object command, Func<AuthDbContext, Task<T>> transition, CancellationToken cancellationToken)
    {
        if (header.ParticipantId != AuthExportInventoryV1.ParticipantId)
            throw new InvalidOperationException("Auth export participant identity is required.");
        var input = JsonSerializer.SerializeToUtf8Bytes(command, Json);
        var inputHash = Hash(input);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var database = new AuthDbContext(options) { LifecycleOnly = true };
            try
            {
                return await database.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await database.Database.BeginTransactionAsync(
                        IsolationLevel.Serializable, cancellationToken);
                    await InitializeAsync(database, transaction, header.OrganisationId, cancellationToken);
                    try
                    {
                        var prior = await database.AuthExportParticipantInboxReceipts.SingleOrDefaultAsync(
                            x => x.MessageId == header.MessageId, cancellationToken);
                        if (prior is not null)
                        {
                            if (prior.OperationId != header.OperationId || prior.OrganisationId != header.OrganisationId
                                || prior.MessageType != messageType || prior.PayloadSha256 != inputHash)
                                throw new InvalidOperationException("Participant inbox identity conflicts with a prior command.");
                            var priorOutput = await database.AuthExportParticipantOutboxMessages.SingleAsync(
                                x => x.OperationId == header.OperationId && x.MessageType == typeof(T).Name,
                                cancellationToken);
                            return DeserializeOutput<T>(priorOutput.PayloadJson);
                        }

                        database.Add(AuthExportParticipantInboxReceipt.Create(header.MessageId, header.OperationId,
                            header.OrganisationId, messageType, inputHash, clock.GetUtcNow()));
                        var output = await transition(database);
                        var payloadJson = JsonSerializer.Serialize(output, Json);
                        database.Add(AuthExportParticipantOutboxMessage.Create(Guid.NewGuid(), header.OperationId,
                            header.OrganisationId, typeof(T).Name, payloadJson,
                            Hash(Encoding.UTF8.GetBytes(payloadJson)), clock.GetUtcNow()));
                        await database.SaveChangesAsync(cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                        return output;
                    }
                    finally
                    {
                        database.LifecycleTransaction = null;
                        database.LifecycleOrganisationId = Guid.Empty;
                    }
                });
            }
            catch (Exception exception) when (IsRetryable(exception)) { }
        }
        throw new InvalidOperationException("Auth export participant could not reconcile a concurrent delivery.");
    }

    private static async Task AcquireBarrierLock(AuthDbContext database, Guid organisationId,
        CancellationToken cancellationToken)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Auth export participant transaction is required.");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation_id, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure } => true,
        DbUpdateException { InnerException: not null } update => IsRetryable(update.InnerException),
        InvalidOperationException { InnerException: not null } wrapper => IsRetryable(wrapper.InnerException),
        _ => false
    };

    private static async Task InitializeAsync(AuthDbContext database, IDbContextTransaction transaction,
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
            throw new InvalidOperationException("Auth participant context initialization failed.");
        database.LifecycleOrganisationId = organisationId;
        database.LifecycleTransaction = transaction.GetDbTransaction();
    }

    private static LifecycleMessageHeaderV1 ReplyHeader(LifecycleMessageHeaderV1 command) => new(
        command.OperationId, command.OrganisationId, command.OperationRevision,
        AuthExportInventoryV1.ParticipantId, LifecycleContractV1.Version,
        Guid.NewGuid(), command.MessageId, command.CorrelationId);
    private static string Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static T DeserializeOutput<T>(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var header = JsonSerializer.Deserialize<LifecycleMessageHeaderV1>(root.GetProperty("header"), Json)
            ?? throw new InvalidOperationException("Durable participant replay header is invalid.");
        object output;
        if (typeof(T) == typeof(OrganisationExportFenceEnteredV1))
            output = new OrganisationExportFenceEnteredV1(header,
                root.GetProperty("fenceToken").GetString()!, root.GetProperty("fenceRevision").GetInt64(),
                root.GetProperty("enteredAt").GetDateTimeOffset());
        else if (typeof(T) == typeof(OrganisationExportFragmentReadyV1))
        {
            var categories = root.GetProperty("categories").EnumerateArray().Select(category =>
                new ExportCategoryFragmentV1(category.GetProperty("category").GetString()!,
                    (ExportCategoryDispositionV1)category.GetProperty("disposition").GetInt32(),
                    category.GetProperty("recordCount").GetInt64(), Optional(category, "schemaVersion"),
                    Optional(category, "contentSha256"), Optional(category, "artifactReference"),
                    Optional(category, "reasonCode"))).ToArray();
            output = new OrganisationExportFragmentReadyV1(header,
                root.GetProperty("snapshotAt").GetDateTimeOffset(),
                root.GetProperty("fenceToken").GetString()!, categories);
        }
        else if (typeof(T) == typeof(OrganisationExportFenceReleasedV1))
            output = new OrganisationExportFenceReleasedV1(header,
                root.GetProperty("fenceToken").GetString()!,
                root.GetProperty("releasedAt").GetDateTimeOffset());
        else throw new InvalidOperationException("Unsupported durable Auth participant output type.");
        return (T)output;
    }

    private static string? Optional(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() : null;
}
