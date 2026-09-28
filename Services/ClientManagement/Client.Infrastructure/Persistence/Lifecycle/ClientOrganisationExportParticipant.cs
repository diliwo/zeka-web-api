using System.Text.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Lifecycle;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace ClientManagement.Infrastructure.Persistence.Lifecycle;

public sealed class ClientOrganisationExportParticipant(
    ApplicationDbContext database,
    ITenantTransactionExecutor transactions,
    IClientExportArtifactStore artifacts,
    IClientExportFixtureScope fixtureScope,
    TimeProvider clock) : IClientOrganisationExportParticipant
{
    private static readonly string[] ExpectedCategories = ClientExportContract.Categories;
    private const string ArtifactSet = "client-management-export-v1";

    public Task<OrganisationExportFenceEnteredV1> EnterFenceAsync(EnterOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateHeader(command.Header);
        fixtureScope.Demand(command.Header.OrganisationId);
        var requestHash = ClientExportCanonical.RequestHash(command);
        return ExecuteReplaySafeAsync(async token =>
        {
            var replay = await Replay<OrganisationExportFenceEnteredV1>(command.Header, requestHash, token);
            if (replay is not null) return replay;
            await AcquireBarrierLock(command.Header.OrganisationId, token);
            replay = await Replay<OrganisationExportFenceEnteredV1>(command.Header, requestHash, token);
            if (replay is not null) return replay;
            var active = await database.OrganisationExportFences.SingleOrDefaultAsync(
                x => x.ReleasedAt == null, token);
            if (active is not null && active.OperationId != command.Header.OperationId)
                throw new InvalidOperationException("organisation_export_fence_active");
            var now = clock.GetUtcNow();
            var fence = active ?? OrganisationExportFence.Enter(command.Header.OperationId,
                command.Header.OrganisationId, command.Header.OperationRevision,
                ClientExportContract.ParticipantId, Guid.NewGuid().ToString("N"), now);
            if (active is null) database.Add(fence);
            if (fence.OperationRevision != command.Header.OperationRevision)
                throw new InvalidOperationException("organisation_export_revision_conflict");
            var response = new OrganisationExportFenceEnteredV1(
                ReplyHeader(command.Header, Guid.NewGuid()), fence.FenceToken, fence.FenceRevision, fence.EnteredAt);
            PersistReply(command.Header, requestHash, response, now);
            await database.SaveChangesAsync(token);
            return response;
        }, cancellationToken);
    }

    public Task<OrganisationExportFragmentReadyV1> StageAsync(StageOrganisationExportV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateHeader(command.Header);
        fixtureScope.Demand(command.Header.OrganisationId);
        var requestHash = ClientExportCanonical.RequestHash(command);
        return ExecuteReplaySafeAsync(async token =>
        {
            await AcquireBarrierLock(command.Header.OrganisationId, token);
            var replay = await Replay<OrganisationExportFragmentReadyV1>(command.Header, requestHash, token);
            if (replay is not null) return replay;
            var fence = await database.OrganisationExportFences.SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, token)
                ?? throw new InvalidOperationException("organisation_export_fence_missing");
            if (fence.ReleasedAt is not null)
                throw new InvalidOperationException("organisation_export_fence_released");
            var ownReceipt = command.FenceEvidence.Receipts.SingleOrDefault(x =>
                x.ParticipantId == ClientExportContract.ParticipantId)
                ?? throw new InvalidOperationException("organisation_export_fence_evidence_missing");
            if (!StringComparer.Ordinal.Equals(ownReceipt.FenceToken, fence.FenceToken)
                || ownReceipt.FenceRevision != fence.FenceRevision
                || ownReceipt.OperationId != fence.OperationId
                || ownReceipt.OrganisationId != fence.OrganisationId)
                throw new InvalidOperationException("organisation_export_fence_evidence_stale");

            var existing = await database.OrganisationExportFragments.AsNoTracking()
                .Where(x => x.OperationId == command.Header.OperationId)
                .OrderBy(x => x.Category).ToArrayAsync(token);
            if (existing.Length != 0)
                throw new InvalidOperationException("organisation_export_fragment_without_inbox_receipt");

            var materialized = await Materialize(command, token);
            var categories = new List<ExportCategoryFragmentV1>(materialized.Count);
            var artifactReceipts = new List<ClientExportArtifactReceipt>();
            var now = clock.GetUtcNow();
            foreach (var item in materialized.OrderBy(x => x.Category, StringComparer.Ordinal))
            {
                string? hash = null;
                string? reference = null;
                string? schema = null;
                if (item.Content is not null)
                {
                    schema = "client-export-csv-v1";
                    var receipt = await artifacts.WriteVerifiedAsync(command.Header.OrganisationId,
                        command.Header.OperationId, new ClientExportArtifactWrite(
                            ArtifactSet, $"{item.Category}.csv", item.Content), token);
                    hash = receipt.ContentSha256;
                    reference = receipt.ArtifactReference;
                    artifactReceipts.Add(receipt);
                }
                var contract = new ExportCategoryFragmentV1(item.Category, item.Disposition,
                    item.RecordCount, schema, hash, reference, item.ReasonCode);
                categories.Add(contract);
                database.Add(OrganisationExportFragment.Create(command.Header.OperationId,
                    command.Header.OrganisationId, command.Header.OperationRevision,
                    ClientExportContract.ParticipantId, item.Category, fence.FenceToken,
                    command.FenceEvidenceHash, command.SnapshotAt, contract.DispositionCode,
                    item.RecordCount, item.SoftDeletedCount, schema, hash, reference,
                    item.ReasonCode, now));
            }
            if (!categories.Select(x => x.Category).SequenceEqual(ExpectedCategories, StringComparer.Ordinal))
                throw new InvalidOperationException("organisation_export_category_inventory_mismatch");
            await artifacts.VerifyExactAsync(command.Header.OrganisationId, command.Header.OperationId,
                ArtifactSet, artifactReceipts, token);
            var response = new OrganisationExportFragmentReadyV1(
                ReplyHeader(command.Header, Guid.NewGuid()), command.SnapshotAt, fence.FenceToken, categories);
            PersistReply(command.Header, requestHash, response, now);
            await database.SaveChangesAsync(token);
            return response;
        }, cancellationToken);
    }

    public Task<OrganisationExportFenceReleasedV1> ReleaseFenceAsync(ReleaseOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default)
    {
        ValidateHeader(command.Header);
        fixtureScope.Demand(command.Header.OrganisationId);
        var requestHash = ClientExportCanonical.RequestHash(command);
        return ExecuteReplaySafeAsync(async token =>
        {
            var replay = await Replay<OrganisationExportFenceReleasedV1>(command.Header, requestHash, token);
            if (replay is not null) return replay;
            await AcquireBarrierLock(command.Header.OrganisationId, token);
            replay = await Replay<OrganisationExportFenceReleasedV1>(command.Header, requestHash, token);
            if (replay is not null) return replay;
            var fence = await database.OrganisationExportFences.AsNoTracking().SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, token)
                ?? throw new InvalidOperationException("organisation_export_fence_missing");
            if (fence.OperationRevision != command.Header.OperationRevision)
                throw new InvalidOperationException("organisation_export_revision_conflict");
            if (!StringComparer.Ordinal.Equals(fence.FenceToken, command.FenceToken))
                throw new InvalidOperationException("organisation_export_fence_token_invalid");
            var now = clock.GetUtcNow();
            await ReleaseFenceRow(fence.OrganisationId, fence.OperationId, fence.FenceToken, now, token);
            var response = new OrganisationExportFenceReleasedV1(
                ReplyHeader(command.Header, Guid.NewGuid()), fence.FenceToken, now);
            PersistReply(command.Header, requestHash, response, now);
            await database.SaveChangesAsync(token);
            return response;
        }, cancellationToken);
    }

    private async Task<T> ExecuteReplaySafeAsync<T>(Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return await transactions.ExecuteAsync(work, cancellationToken); }
            catch (Exception exception) when (IsRetryable(exception)) { }
        }
        throw new InvalidOperationException("Client export participant could not reconcile a concurrent delivery.");
    }

    private static bool IsRetryable(Exception exception) => exception switch
    {
        PostgresException { SqlState: PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.SerializationFailure } => true,
        DbUpdateException { InnerException: not null } update => IsRetryable(update.InnerException),
        InvalidOperationException { InnerException: not null } wrapper => IsRetryable(wrapper.InnerException),
        _ => false
    };

    private async Task<List<MaterializedCategory>> Materialize(StageOrganisationExportV1 command,
        CancellationToken token)
    {
        var result = new List<MaterializedCategory>();
        var organisation = command.Header.OrganisationId;
        result.Add(await Beneficiaries(organisation, token));
        result.Add(await CaseHistories(organisation, token));
        result.Add(await Assessments(organisation, token));
        result.Add(await Reports(organisation, token));
        result.Add(await SchoolHistory(organisation, token));
        result.Add(await ProfessionalHistory(organisation, token));
        result.Add(await Assignments(organisation, command.SnapshotAt, token));
        result.Add(Absent("notes", ExportCategoryDispositionV1.Withheld, "disclosure-policy-withheld"));
        result.Add(Absent("generated-report-artifacts", ExportCategoryDispositionV1.NotImplemented, "category-not-implemented"));
        result.Add(Absent("audit-events", ExportCategoryDispositionV1.NotImplemented, "category-not-implemented"));
        result.Add(Absent("integration-events", ExportCategoryDispositionV1.NotImplemented, "category-not-implemented"));
        return result;
    }

    private async Task<MaterializedCategory> Beneficiaries(Guid organisation, CancellationToken token)
        => await QueryCsv("beneficiaries",
            ["id", "reference_number", "first_name", "last_name", "birth_date", "nationality", "soft_deleted"],
            """SELECT "Id"::text, "ReferenceNumber", "FirstName", "LastName", to_char("BirthDate", 'YYYY-MM-DD'), "Nationality", CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."Clients" WHERE "OrganisationId" = @organisation_id ORDER BY "Id";""" ,
            organisation, token);

    private async Task<MaterializedCategory> CaseHistories(Guid organisation, CancellationToken token)
        => await QueryCsv("case-histories",
            ["id", "beneficiary_id", "staff_id", "start_date", "end_date", "closure_reason", "soft_deleted"],
            """SELECT "SchoolRegistrationId"::text, "ClientId"::text, "SocialWorkerId"::text, to_char("StartDate", 'YYYY-MM-DD'), CASE WHEN "EndDate" IS NULL THEN NULL ELSE to_char("EndDate", 'YYYY-MM-DD') END, "ReasonOfClosure", CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."SocialCases" WHERE "OrganisationId" = @organisation_id ORDER BY "SchoolRegistrationId";""",
            organisation, token);

    private async Task<MaterializedCategory> Assessments(Guid organisation, CancellationToken token)
        => await QueryCsv("structured-assessments", ["id", "beneficiary_id", "is_finalized", "soft_deleted"],
            """SELECT "AssessmentId"::text, "ClientId"::text, CASE WHEN "IsFinalized" THEN 'true' ELSE 'false' END, CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."Assessments" WHERE "OrganisationId" = @organisation_id ORDER BY "AssessmentId";""",
            organisation, token);

    private async Task<MaterializedCategory> Reports(Guid organisation, CancellationToken token)
        => await QueryCsv("structured-reports",
            ["id", "beneficiary_id", "staff_id", "action_id", "action_date", "soft_deleted"],
            """SELECT "Id"::text, "ClientId"::text, "SocialWorkerId"::text, "MonitoringActionId"::text, to_char("ActionDate", 'YYYY-MM-DD'), CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."MonitoringReports" WHERE "OrganisationId" = @organisation_id ORDER BY "Id";""",
            organisation, token);

    private async Task<MaterializedCategory> SchoolHistory(Guid organisation, CancellationToken token)
        => await QueryCsv("school-history",
            ["id", "beneficiary_id", "school_id", "training_id", "training_type_id", "start_date", "end_date", "course_level", "result", "soft_deleted"],
            """SELECT "SchoolRegistrationId"::text, "ClientId"::text, "SchoolId"::text, "TrainingId"::text, "TrainingTypeId"::text, to_char("StartDate", 'YYYY-MM-DD'), to_char("EnDate", 'YYYY-MM-DD'), "CourseLevel"::text, "Result"::text, CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."SchoolRegistrations" WHERE "OrganisationId" = @organisation_id ORDER BY "SchoolRegistrationId";""",
            organisation, token);

    private async Task<MaterializedCategory> ProfessionalHistory(Guid organisation, CancellationToken token)
        => await QueryCsv("professional-history",
            ["id", "beneficiary_id", "start_date", "end_date", "company", "function", "contract_type", "nature_id", "soft_deleted"],
            """SELECT "ProfessionnalExperienceId"::text, "ClientId"::text, to_char("StartDate", 'YYYY-MM-DD'), to_char("EndDate", 'YYYY-MM-DD'), "CompanyName", "Function", "TypeOfContract"::text, "NatureOfContractId"::text, CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."ProfessionnalExperience" WHERE "OrganisationId" = @organisation_id ORDER BY "ProfessionnalExperienceId";""",
            organisation, token);

    private async Task<MaterializedCategory> Assignments(Guid organisation, DateTimeOffset snapshotAt,
        CancellationToken token)
        => await QueryCsv("beneficiary-assignments",
            ["case_id", "beneficiary_id", "staff_id", "start_date", "end_date", "active_at_snapshot", "soft_deleted"],
            """SELECT "SchoolRegistrationId"::text, "ClientId"::text, "SocialWorkerId"::text, to_char("StartDate", 'YYYY-MM-DD'), CASE WHEN "EndDate" IS NULL THEN NULL ELSE to_char("EndDate", 'YYYY-MM-DD') END, CASE WHEN NOT "Softdelete" AND "StartDate"::date <= @snapshot_at AND ("EndDate" IS NULL OR "EndDate"::date > @snapshot_at) THEN 'true' ELSE 'false' END, CASE WHEN "Softdelete" THEN 'true' ELSE 'false' END FROM public."SocialCases" WHERE "OrganisationId" = @organisation_id ORDER BY "SchoolRegistrationId";""",
            organisation, token, snapshotAt.UtcDateTime.Date);

    private async Task<MaterializedCategory> QueryCsv(string category, string[] headers, string sql,
        Guid organisation, CancellationToken token, DateTime? snapshotAt = null)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("organisation_export_transaction_missing");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var organisationParameter = command.CreateParameter();
        organisationParameter.ParameterName = "organisation_id";
        organisationParameter.Value = organisation;
        command.Parameters.Add(organisationParameter);
        if (snapshotAt.HasValue)
        {
            var snapshotParameter = command.CreateParameter();
            snapshotParameter.ParameterName = "snapshot_at";
            snapshotParameter.Value = snapshotAt.Value;
            command.Parameters.Add(snapshotParameter);
        }
        var rows = new List<IReadOnlyList<string?>>();
        long softDeleted = 0;
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var row = new string?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = await reader.IsDBNullAsync(index, token) ? null : reader.GetString(index);
            if (StringComparer.Ordinal.Equals(row[^1], "true")) softDeleted++;
            rows.Add(row);
        }
        return Csv(category, headers, rows, softDeleted);
    }
    private static MaterializedCategory Csv(string category, string[] headers,
        IEnumerable<IReadOnlyList<string?>> rows, long softDeleted)
    {
        var values = rows.ToArray();
        var bytes = ClientExportCanonical.Csv(headers, values);
        return new(category, values.Length == 0 ? ExportCategoryDispositionV1.Empty : ExportCategoryDispositionV1.Included,
            values.Length, softDeleted, bytes, null);
    }

    private static MaterializedCategory Absent(string category, ExportCategoryDispositionV1 disposition,
        string reason) => new(category, disposition, 0, 0, null, reason);

    private async Task<T?> Replay<T>(LifecycleMessageHeaderV1 header, string requestHash, CancellationToken token)
        where T : class
    {
        var receipt = await database.OrganisationExportInbox.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MessageId == header.MessageId, token);
        if (receipt is null) return null;
        if (!StringComparer.Ordinal.Equals(receipt.RequestHash, requestHash)
            || !StringComparer.Ordinal.Equals(receipt.ResponseType, typeof(T).FullName))
            throw new InvalidOperationException("organisation_export_message_identity_conflict");
        if (typeof(T) == typeof(OrganisationExportFragmentReadyV1))
            return (T)(object)DeserializeFragment(receipt.ResponseJson);
        return JsonSerializer.Deserialize<T>(receipt.ResponseJson, ClientExportCanonical.Json)
            ?? throw new InvalidOperationException("organisation_export_receipt_is_invalid");
    }

    private static OrganisationExportFragmentReadyV1 DeserializeFragment(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var header = root.GetProperty("header").Deserialize<LifecycleMessageHeaderV1>(ClientExportCanonical.Json)
            ?? throw new InvalidOperationException("organisation_export_receipt_is_invalid");
        var categories = root.GetProperty("categories").EnumerateArray().Select(category =>
            new ExportCategoryFragmentV1(
                category.GetProperty("category").GetString()!,
                category.GetProperty("disposition").Deserialize<ExportCategoryDispositionV1>(ClientExportCanonical.Json),
                category.GetProperty("recordCount").GetInt64(),
                OptionalString(category, "schemaVersion"),
                OptionalString(category, "contentSha256"),
                OptionalString(category, "artifactReference"),
                OptionalString(category, "reasonCode"))).ToArray();
        return new OrganisationExportFragmentReadyV1(header,
            root.GetProperty("snapshotAt").GetDateTimeOffset(), root.GetProperty("fenceToken").GetString()!, categories);
    }

    private static string? OptionalString(JsonElement value, string property) =>
        value.GetProperty(property).ValueKind == JsonValueKind.Null ? null : value.GetProperty(property).GetString();

    private void PersistReply<T>(LifecycleMessageHeaderV1 request, string requestHash, T response,
        DateTimeOffset completedAt) where T : class
    {
        var responseHeader = response switch
        {
            OrganisationExportFenceEnteredV1 entered => entered.Header,
            OrganisationExportFragmentReadyV1 ready => ready.Header,
            OrganisationExportFenceReleasedV1 released => released.Header,
            _ => throw new InvalidOperationException("Unsupported export participant response.")
        };
        var json = JsonSerializer.Serialize(response, ClientExportCanonical.Json);
        database.Add(OrganisationExportCommandReceipt.Complete(request.OrganisationId, request.MessageId,
            request.OperationId, request.OperationRevision, requestHash, typeof(T).FullName!, json,
            responseHeader.MessageId, completedAt));
        database.Add(OrganisationExportOutboxMessage.Stage(request.OrganisationId, responseHeader.MessageId,
            request.OperationId, typeof(T).FullName!, json,
            ClientExportCanonical.Sha256(System.Text.Encoding.UTF8.GetBytes(json)), completedAt));
    }

    private async Task AcquireBarrierLock(Guid organisationId, CancellationToken token)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("organisation_export_transaction_missing");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation_id, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "organisation_id";
        parameter.Value = organisationId.ToString("D");
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(token);
    }

    private async Task ReleaseFenceRow(Guid organisationId, Guid operationId, string fenceToken,
        DateTimeOffset releasedAt, CancellationToken token)
    {
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("organisation_export_transaction_missing");
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE public."OrganisationExportFences"
            SET "ReleasedAt" = @released_at
            WHERE "OrganisationId" = @organisation_id
              AND "OperationId" = @operation_id
              AND "FenceToken" = @fence_token
              AND "ReleasedAt" IS NULL
            """;
        AddParameter(command, "released_at", releasedAt);
        AddParameter(command, "organisation_id", organisationId);
        AddParameter(command, "operation_id", operationId);
        AddParameter(command, "fence_token", fenceToken);
        if (await command.ExecuteNonQueryAsync(token) != 1)
            throw new InvalidOperationException("organisation_export_fence_release_conflict");
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static LifecycleMessageHeaderV1 ReplyHeader(LifecycleMessageHeaderV1 request, Guid messageId) =>
        new(request.OperationId, request.OrganisationId, request.OperationRevision,
            request.ParticipantId, request.ContractVersion, messageId, request.MessageId, request.CorrelationId);

    private static void ValidateHeader(LifecycleMessageHeaderV1 header)
    {
        if (!StringComparer.Ordinal.Equals(header.ParticipantId, ClientExportContract.ParticipantId)
            || header.ContractVersion != LifecycleContractV1.Version)
            throw new InvalidOperationException("organisation_export_participant_identity_invalid");
    }

    private sealed record MaterializedCategory(string Category, ExportCategoryDispositionV1 Disposition,
        long RecordCount, long SoftDeletedCount, byte[]? Content, string? ReasonCode);
}
