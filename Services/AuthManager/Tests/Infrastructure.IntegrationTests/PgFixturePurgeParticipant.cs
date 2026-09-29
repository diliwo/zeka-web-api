using System.Data;
using System.Text.Json;
using AuthManager.Application.Lifecycle;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Isolated conformance adapter, never registered by a production service. Its one-item-per-
/// category fixture cannot be used as evidence that real owner tables or files are disposable.
/// </summary>
internal sealed class PgFixturePurgeParticipant(string connectionString, string participantId,
    string? documentFixturePath = null)
    : INonProductionPurgeParticipant
{
    public string ParticipantId => participantId;
    public bool CrashBeforeLocalCommit { get; set; }
    public bool CrashAfterLocalCommit { get; set; }
    public bool FailFileDeletion { get; set; }

    public async Task AcceptStartAsync(PurgeCommandV1 start, CancellationToken cancellationToken)
    {
        if (start.Kind != PurgeCommandKindV1.IrreversibleStarted || start.ParticipantId != ""
            || start.CapabilityKey != "organisation.purge-started" || start.Category != ""
            || start.ItemId != ""
            || start.IdempotencyId != start.MessageId || start.OrganisationId == Guid.Empty
            || start.TerminationOperationId == Guid.Empty || start.PlanId == Guid.Empty
            || start.MessageId != PurgeCommandV1.StartId(start.PlanId))
            throw new InvalidOperationException("Invalid irreversible-start fact.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await SetContext(connection, transaction, start.OrganisationId, cancellationToken);
        await using var command = new NpgsqlCommand("""
            INSERT INTO life04a_fixture."Starts"
              ("OrganisationId", "ParticipantId", "OperationId", "Revision", "RegistryRevision",
               "PlanId", "PlanHash", "DecisionSetId", "DecisionSetHash", "StartHash")
            VALUES (@org,@participant,@operation,@revision,@registry,@plan,@planHash,
                    @set,@setHash,@startHash)
            ON CONFLICT ("OrganisationId", "ParticipantId", "OperationId") DO NOTHING
            """, connection, transaction);
        command.Parameters.AddWithValue("org", start.OrganisationId);
        command.Parameters.AddWithValue("participant", participantId);
        command.Parameters.AddWithValue("operation", start.TerminationOperationId);
        command.Parameters.AddWithValue("revision", start.OperationRevision);
        command.Parameters.AddWithValue("registry", start.RegistryRevision);
        command.Parameters.AddWithValue("plan", start.PlanId);
        command.Parameters.AddWithValue("planHash", start.PlanHash);
        command.Parameters.AddWithValue("set", start.DecisionSetId);
        command.Parameters.AddWithValue("setHash", start.DecisionSetHash);
        command.Parameters.AddWithValue("startHash", start.PayloadHash());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var verify = new NpgsqlCommand("""
            SELECT "StartHash" FROM life04a_fixture."Starts"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "OperationId"=@operation AND "Revision"=@revision
              AND "RegistryRevision"=@registry AND "PlanId"=@plan
              AND "PlanHash"=@planHash AND "DecisionSetId"=@set
              AND "DecisionSetHash"=@setHash
            """, connection, transaction);
        foreach (NpgsqlParameter parameter in command.Parameters)
            verify.Parameters.AddWithValue(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        if (await verify.ExecuteScalarAsync(cancellationToken) is not string hash
            || hash != start.PayloadHash())
            throw new InvalidOperationException("Conflicting irreversible-start fact.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PurgeParticipantReceiptV1> ExecuteAsync(PurgeCommandV1 command,
        CancellationToken cancellationToken)
    {
        if (command.Kind != PurgeCommandKindV1.ParticipantPurge
            || command.ParticipantId != participantId
            || command.CapabilityKey != "organisation.disposition-category"
            || command.IdempotencyId != command.MessageId || command.Category == ""
            || command.ItemId != $"fixture:{command.Category}"
            || command.MessageId != PurgeCommandV1.CommandId(command.PlanId,
                command.ParticipantId, command.Category, command.ItemId))
            throw new InvalidOperationException("Wrong participant or destructive-command identity.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await SetContext(connection, transaction, command.OrganisationId, cancellationToken);
        await using (var start = new NpgsqlCommand("""
            SELECT count(*) FROM life04a_fixture."Starts"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "OperationId"=@operation AND "Revision"=@revision
              AND "RegistryRevision"=@registry AND "PlanId"=@plan
              AND "PlanHash"=@planHash AND "DecisionSetId"=@set
              AND "DecisionSetHash"=@setHash
            """, connection, transaction))
        {
            Bind(start, command);
            if (Convert.ToInt64(await start.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("Start fact absent or command is stale/reordered.");
        }
        await using (var replay = new NpgsqlCommand("""
            SELECT "CommandHash", "ReceiptJson" FROM life04a_fixture."Inbox"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "OperationId"=@operation AND "Category"=@category AND "ItemId"=@item
            """, connection, transaction))
        {
            Bind(replay, command);
            await using var reader = await replay.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var hash = reader.GetString(0);
                var json = reader.GetString(1);
                if (hash != command.PayloadHash())
                    throw new InvalidOperationException("Conflicting category command after local commit.");
                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
                if (documentFixturePath is not null)
                    return await CompleteDocumentAsync(command, cancellationToken);
                return JsonSerializer.Deserialize<PurgeParticipantReceiptV1>(json)
                    ?? throw new InvalidOperationException("Local receipt is unreadable.");
            }
        }
        await using (var allowed = new NpgsqlCommand("""
            SELECT count(*) FROM life04a_fixture."Items"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "Category"=@category AND "ItemId"=@item AND "Disposition"='PURGE'
            """, connection, transaction))
        {
            Bind(allowed, command);
            if (Convert.ToInt64(await allowed.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("No exact synthetic PURGE item exists for this category.");
        }
        await using var deletion = new NpgsqlCommand("""
            DELETE FROM life04a_fixture."Payloads"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "Category"=@category AND "ItemId"=@item
            """, connection, transaction);
        Bind(deletion, command);
        var changed = await deletion.ExecuteNonQueryAsync(cancellationToken);
        var state = changed == 1 ? PurgeOutcomeV1.Purged : PurgeOutcomeV1.AlreadyAbsent;
        var proof = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"synthetic-local-proof-v1|{command.OrganisationId:D}|{participantId}|{command.Category}|{command.ItemId}|{state}")))
            .ToLowerInvariant();
        var receipt = new PurgeParticipantReceiptV1(Guid.NewGuid(), command.MessageId,
            command.OrganisationId, command.TerminationOperationId, command.OperationRevision,
            command.RegistryRevision, command.PlanId, command.PlanHash,
            command.DecisionSetId, command.DecisionSetHash, participantId,
            command.CapabilityKey, command.Category, command.ItemId,
            command.IdempotencyId,
            state, proof, null);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO life04a_fixture."Inbox"
              ("OrganisationId", "ParticipantId", "OperationId", "Category", "ItemId", "MessageId",
               "CommandHash", "ReceiptJson", "Outcome")
            VALUES (@org,@participant,@operation,@category,@item,@message,@commandHash,@receipt,@outcome)
            """, connection, transaction);
        Bind(insert, command);
        insert.Parameters.AddWithValue("message", command.MessageId);
        insert.Parameters.AddWithValue("commandHash", command.PayloadHash());
        insert.Parameters.AddWithValue("receipt", documentFixturePath is null
            ? JsonSerializer.Serialize(receipt) : "");
        insert.Parameters.AddWithValue("outcome", (int)state);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        if (CrashBeforeLocalCommit)
        {
            CrashBeforeLocalCommit = false;
            throw new InvalidOperationException("Synthetic crash before local commit.");
        }
        await transaction.CommitAsync(cancellationToken);
        if (CrashAfterLocalCommit)
        {
            CrashAfterLocalCommit = false;
            throw new InvalidOperationException("Synthetic crash after local commit.");
        }
        return documentFixturePath is null
            ? receipt : await CompleteDocumentAsync(command, cancellationToken);
    }

    private async Task<PurgeParticipantReceiptV1> CompleteDocumentAsync(PurgeCommandV1 command,
        CancellationToken cancellationToken)
    {
        if (documentFixturePath is null || participantId != "admin-area-documents"
            || command.Category != "admin-area-document-storage")
            throw new InvalidOperationException("Document fixture owner mismatch.");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        await SetContext(connection, transaction, command.OrganisationId, cancellationToken);
        await using var query = new NpgsqlCommand("""
            SELECT "CommandHash", "ReceiptJson", "Outcome", "FileAttempts"
            FROM life04a_fixture."Inbox"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "OperationId"=@operation AND "Category"=@category AND "ItemId"=@item
            FOR UPDATE
            """, connection, transaction);
        Bind(query, command);
        string existing;
        int outcome;
        int attempts;
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)
                || reader.GetString(0) != command.PayloadHash())
                throw new InvalidOperationException("Document pending ledger conflicts with command.");
            existing = reader.GetString(1);
            outcome = reader.GetInt32(2);
            attempts = reader.GetInt32(3);
        }
        if (existing != "")
        {
            if (JsonSerializer.Deserialize<PurgeParticipantReceiptV1>(existing) is { State:
                    PurgeOutcomeV1.Purged or PurgeOutcomeV1.AlreadyAbsent }
                && File.Exists(documentFixturePath))
                throw new InvalidOperationException("Document reappeared after a completed local receipt.");
            return JsonSerializer.Deserialize<PurgeParticipantReceiptV1>(existing)
                ?? throw new InvalidOperationException("Document receipt is unreadable.");
        }
        if (outcome != (int)PurgeOutcomeV1.Purged
            && outcome != (int)PurgeOutcomeV1.AlreadyAbsent)
            throw new InvalidOperationException("Document pending outcome is invalid.");
        var fileFailed = FailFileDeletion;
        FailFileDeletion = false;
        if (!fileFailed)
        {
            // The path is created under a fresh test-owned temporary root, never from a
            // message or production metadata. Deletion is idempotent after restart.
            try { File.Delete(documentFixturePath); }
            catch (IOException) { fileFailed = true; }
            catch (UnauthorizedAccessException) { fileFailed = true; }
            if (File.Exists(documentFixturePath)) fileFailed = true;
        }
        var reportedState = fileFailed
            ? attempts + 1 >= 3 ? PurgeOutcomeV1.InterventionRequired
                : PurgeOutcomeV1.FailedRetryable
            : (PurgeOutcomeV1)outcome;
        var proof = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"synthetic-document-proof-v1|{command.OrganisationId:D}|{command.Category}|{command.ItemId}|{outcome}|{reportedState}|{attempts + 1}")))
            .ToLowerInvariant();
        var receipt = new PurgeParticipantReceiptV1(Guid.NewGuid(), command.MessageId,
            command.OrganisationId, command.TerminationOperationId, command.OperationRevision,
            command.RegistryRevision, command.PlanId, command.PlanHash,
            command.DecisionSetId, command.DecisionSetHash, participantId,
            command.CapabilityKey, command.Category, command.ItemId,
            command.IdempotencyId,
            reportedState, proof, fileFailed ? "synthetic-file-unavailable" : null);
        await using var update = new NpgsqlCommand("""
            UPDATE life04a_fixture."Inbox"
            SET "ReceiptJson"=@receipt, "FileAttempts"="FileAttempts"+1,
                "LastFailure"=@failure
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "OperationId"=@operation AND "Category"=@category AND "ItemId"=@item
            """, connection, transaction);
        Bind(update, command);
        update.Parameters.AddWithValue("receipt", fileFailed
            && reportedState == PurgeOutcomeV1.FailedRetryable
            ? "" : JsonSerializer.Serialize(receipt));
        update.Parameters.AddWithValue("failure", fileFailed
            ? "synthetic-file-unavailable" : DBNull.Value);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return receipt;
    }

    private static void Bind(NpgsqlCommand sql, PurgeCommandV1 message)
    {
        sql.Parameters.AddWithValue("org", message.OrganisationId);
        sql.Parameters.AddWithValue("participant", message.ParticipantId);
        sql.Parameters.AddWithValue("operation", message.TerminationOperationId);
        sql.Parameters.AddWithValue("category", message.Category);
        sql.Parameters.AddWithValue("item", message.ItemId);
        sql.Parameters.AddWithValue("revision", message.OperationRevision);
        sql.Parameters.AddWithValue("registry", message.RegistryRevision);
        sql.Parameters.AddWithValue("plan", message.PlanId);
        sql.Parameters.AddWithValue("planHash", message.PlanHash);
        sql.Parameters.AddWithValue("set", message.DecisionSetId);
        sql.Parameters.AddWithValue("setHash", message.DecisionSetHash);
    }

    private static async Task SetContext(NpgsqlConnection connection,
        NpgsqlTransaction transaction, Guid organisationId, CancellationToken cancellationToken)
    {
        await using var sql = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @org, true)", connection, transaction);
        sql.Parameters.AddWithValue("org", organisationId.ToString("D"));
        await sql.ExecuteScalarAsync(cancellationToken);
    }
}
