using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdminAreaManagement.Application.Common.Authorization;
using AdminAreaManagement.Application.Exports;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Core.ValueObjects;
using AdminAreaManagement.Infrastructure.Persistence;
using AdminAreaManagement.Infrastructure.Persistence.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AdminAreaManagement.Infrastructure.Exports;

internal sealed class AdminAreaExportParticipant(
    ApplicationDbContext database,
    ITenantTransactionExecutor transactions,
    IAdminAreaExportArtifactStore artifacts,
    IFileService files,
    AdminAreaFixtureScope fixture,
    TenantTransactionAttemptState attempt,
    TimeProvider clock) : IAdminAreaExportParticipant
{
    private const long AdvisoryLockSeed = 4601;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrganisationExportFenceEnteredV1> EnterFenceAsync(
        EnterOrganisationExportFenceV1 command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        DemandHeader(command.Header, AdminAreaExportContractV1.ParticipantId);
        fixture.Demand(command.Header.OrganisationId);
        return transactions.ExecuteAsync(async token =>
        {
            await AcquireOrganisationLockAsync(command.Header.OrganisationId, token);
            var requestHash = RequestHash("enter-fence", command.Header);
            var duplicate = await DemandInboxReplayAsync(command.Header, "enter-fence", requestHash, token);
            var existing = await database.AdminAreaExportFences.SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, token);
            if (duplicate)
                return FenceReceipt(command.Header, existing
                    ?? throw new InvalidOperationException("Inbox replay is missing its durable fence."));

            var active = await database.AdminAreaExportFences.SingleOrDefaultAsync(x => x.ReleasedAt == null, token);
            if (active is not null)
            {
                if (active.OperationId != command.Header.OperationId
                    || active.OperationRevision != command.Header.OperationRevision)
                    throw new InvalidOperationException("Another export fence is already active for this organisation.");
                await RecordInboxAndOutboxAsync(command.Header, "enter-fence", requestHash,
                    FenceReceipt(command.Header, active), token);
                return FenceReceipt(command.Header, active);
            }

            var unresolvedFiles = await database.DocumentPartners.AsNoTracking().AnyAsync(x =>
                x.FileWriteState != DocumentFileOperationState.Completed
                || (x.FileDeleteState != null && x.FileDeleteState != DocumentFileOperationState.Completed), token);
            if (unresolvedFiles)
                throw new InvalidOperationException("Pending or failed document file operations block the export fence.");

            var revision = (await database.AdminAreaExportFences.MaxAsync(
                x => (long?)x.FenceRevision, token) ?? 0) + 1;
            var enteredAt = UtcMicrosecond(clock.GetUtcNow());
            var fence = new AdminAreaExportFence(command.Header.OperationId, command.Header.OrganisationId,
                command.Header.OperationRevision, DeterministicToken(command.Header, revision), revision, enteredAt);
            database.AdminAreaExportFences.Add(fence);
            var response = FenceReceipt(command.Header, fence);
            await RecordInboxAndOutboxAsync(command.Header, "enter-fence", requestHash, response, token);
            return response;
        }, cancellationToken);
    }

    public Task<OrganisationExportFragmentReadyV1> StageFragmentAsync(
        StageOrganisationExportV1 command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Header.ParticipantId is not (AdminAreaExportContractV1.ParticipantId
            or AdminAreaExportContractV1.DocumentParticipantId))
            throw new InvalidOperationException("The stage command targets an unknown AdminArea participant.");
        if (command.FenceOwnerParticipantId != AdminAreaExportContractV1.ParticipantId)
            throw new InvalidOperationException("AdminArea export fragments must use the AdminArea fence owner.");
        DemandHeader(command.Header, command.Header.ParticipantId);
        fixture.Demand(command.Header.OrganisationId);
        return transactions.ExecuteAsync(async token =>
        {
            await AcquireOrganisationLockAsync(command.Header.OrganisationId, token);
            var requestHash = RequestHash("stage-fragment", command.Header, command.SnapshotAt,
                command.FenceEvidenceHash);
            var duplicate = await DemandInboxReplayAsync(command.Header, "stage-fragment", requestHash, token);
            var persisted = await database.AdminAreaExportFragments.SingleOrDefaultAsync(x =>
                x.OperationId == command.Header.OperationId && x.ParticipantId == command.Header.ParticipantId, token);
            if (duplicate)
                return DeserializeFragment(command.Header, persisted
                    ?? throw new InvalidOperationException("Inbox replay is missing its durable fragment."));

            var fence = await database.AdminAreaExportFences.SingleOrDefaultAsync(x =>
                x.OperationId == command.Header.OperationId && x.ReleasedAt == null, token)
                ?? throw new InvalidOperationException("The AdminArea export fence is not active.");
            if (fence.OperationRevision != command.Header.OperationRevision)
                throw new InvalidOperationException("The active fence belongs to another operation revision.");
            var receipt = command.FenceEvidence.Receipts.SingleOrDefault(x =>
                x.ParticipantId == command.FenceOwnerParticipantId)
                ?? throw new InvalidOperationException("The stage command has no participant fence receipt.");
            if (!StringComparer.Ordinal.Equals(receipt.FenceToken, fence.FenceToken)
                || receipt.FenceRevision != fence.FenceRevision || receipt.EnteredAt != fence.EnteredAt)
                throw new InvalidOperationException("The stage command does not carry the active AdminArea fence.");

            var categories = command.Header.ParticipantId == AdminAreaExportContractV1.ParticipantId
                ? await BuildStructuredCategoriesAsync(command.Header, token)
                : await BuildDocumentCategoryAsync(command.Header, token);
            var response = new OrganisationExportFragmentReadyV1(
                command.Header, command.SnapshotAt, fence.FenceToken, categories);
            var payload = JsonSerializer.Serialize(response, Json);
            database.AdminAreaExportFragments.Add(new AdminAreaExportFragment(
                command.Header.OperationId, command.Header.OrganisationId, command.Header.ParticipantId,
                command.SnapshotAt, fence.FenceToken, response.FragmentHash, payload));
            await RecordInboxAndOutboxAsync(command.Header, "stage-fragment", requestHash, response, token);
            return response;
        }, cancellationToken);
    }

    public Task<OrganisationExportFenceReleasedV1> ReleaseFenceAsync(
        ReleaseOrganisationExportFenceV1 command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        DemandHeader(command.Header, AdminAreaExportContractV1.ParticipantId);
        fixture.Demand(command.Header.OrganisationId);
        return transactions.ExecuteAsync(async token =>
        {
            await AcquireOrganisationLockAsync(command.Header.OrganisationId, token);
            var requestHash = RequestHash("release-fence", command.Header, command.FenceToken);
            var duplicate = await DemandInboxReplayAsync(command.Header, "release-fence", requestHash, token);
            var fence = await database.AdminAreaExportFences.SingleOrDefaultAsync(
                x => x.OperationId == command.Header.OperationId, token)
                ?? throw new InvalidOperationException("The AdminArea export fence does not exist.");
            if (!StringComparer.Ordinal.Equals(fence.FenceToken, command.FenceToken))
                throw new InvalidOperationException("The fence token does not match the active operation.");
            if (duplicate)
                return Released(command.Header, fence);
            if (fence.ReleasedAt is null) fence.Release(UtcMicrosecond(clock.GetUtcNow()));
            var response = Released(command.Header, fence);
            await RecordInboxAndOutboxAsync(command.Header, "release-fence", requestHash, response, token);
            return response;
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<ExportCategoryFragmentV1>> BuildStructuredCategoriesAsync(
        LifecycleMessageHeaderV1 header, CancellationToken cancellationToken)
    {
        var teams = (await database.Teams.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.Name, x.Acronym, x.Softdelete }).ToArray();
        var staff = (await database.StaffMembers.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.OrganisationMembershipId, x.TeamId, x.FirstName, x.LastName,
                x.UserName, x.ProjectionVersion, x.Softdelete }).ToArray();
        var partners = (await database.Partners.AsNoTracking().OrderBy(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.PartnerNumber, x.Name, x.StaffMemberId, x.CategoryOfPartner,
                x.CategoryOfPartnerName, x.StatusOfPartner, x.DateOfAgreementSignature, x.DateOfConclusion,
                x.IsEconomieSociale, x.Note, x.Softdelete }).ToArray();
        var contacts = (await database.Set<ContactPerson>().AsNoTracking()
                .OrderBy(x => x.PartnerId).ThenBy(x => x.ContactDetails).ThenBy(x => x.ContactName)
                .ToListAsync(cancellationToken))
            .Select(x => new { x.PartnerId, x.ContactDetails, x.ContactName, x.Gender, x.ToDelete }).ToArray();
        var emails = (await database.Set<Email>().AsNoTracking()
                .OrderBy(x => x.PartnerId).ThenBy(x => x.Id).ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.PartnerId, x.EmailAddress }).ToArray();
        var documents = (await database.DocumentPartners.AsNoTracking().OrderBy(x => x.Id)
                .ToListAsync(cancellationToken))
            .Select(x => new { x.Id, x.PartnerId, x.Name, x.Description, x.ContentType, x.CreateOperationId,
                x.CreateRequestHash, x.FileWriteState, x.DeleteOperationId, x.FileDeleteState, x.Softdelete }).ToArray();

        MaterializedCategory[] materialized =
        [
            await MaterializeCategoryAsync(header, "partner-contacts", contacts, cancellationToken),
            await MaterializeCategoryAsync(header, "partner-document-metadata", documents, cancellationToken),
            await MaterializeCategoryAsync(header, "partner-emails", emails, cancellationToken),
            await MaterializeCategoryAsync(header, "partners", partners, cancellationToken),
            await MaterializeCategoryAsync(header, "staff-members", staff, cancellationToken),
            await MaterializeCategoryAsync(header, "teams", teams, cancellationToken)
        ];
        await artifacts.VerifyExactAsync(header.OrganisationId, header.OperationId,
            AdminAreaExportContractV1.ParticipantId, materialized.Select(x => x.Receipt).ToArray(), cancellationToken);
        return materialized.Select(x => x.Category).ToArray();
    }

    private async Task<IReadOnlyList<ExportCategoryFragmentV1>> BuildDocumentCategoryAsync(
        LifecycleMessageHeaderV1 header, CancellationToken cancellationToken)
    {
        var documents = await database.DocumentPartners.AsNoTracking().OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        if (documents.Any(x => x.FileWriteState != DocumentFileOperationState.Completed
            || (x.FileDeleteState != null && x.FileDeleteState != DocumentFileOperationState.Completed)))
            throw new InvalidOperationException("Unresolved document file operations block artifact staging.");

        using var stream = new MemoryStream();
        await using (var writer = new BinaryWriterStream(stream))
        {
            foreach (var document in documents.Where(x => !x.Softdelete))
            {
                var content = files.GetContentFile(header.OrganisationId, document.PartnerId, document.Id);
                await writer.WriteInt32Async(document.Id, cancellationToken);
                await writer.WriteInt32Async(document.PartnerId, cancellationToken);
                await writer.WriteBytesAsync(Encoding.UTF8.GetBytes(document.Name ?? string.Empty), cancellationToken);
                await writer.WriteBytesAsync(content, cancellationToken);
            }
        }
        var receipt = await artifacts.WriteVerifiedAsync(header.OrganisationId, header.OperationId,
            new ExportArtifactWrite(AdminAreaExportContractV1.DocumentParticipantId,
                "partner-document-artifacts", stream.ToArray()), cancellationToken);
        await artifacts.VerifyExactAsync(header.OrganisationId, header.OperationId,
            AdminAreaExportContractV1.DocumentParticipantId, [receipt], cancellationToken);
        return [Category("partner-document-artifacts", documents.Count(x => !x.Softdelete), receipt)];
    }

    private async Task<MaterializedCategory> MaterializeCategoryAsync<T>(LifecycleMessageHeaderV1 header,
        string category, IReadOnlyCollection<T> records, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(records, Json);
        var receipt = await artifacts.WriteVerifiedAsync(header.OrganisationId, header.OperationId,
            new ExportArtifactWrite(AdminAreaExportContractV1.ParticipantId, category, content), cancellationToken);
        return new MaterializedCategory(Category(category, records.Count, receipt), receipt);
    }

    private static ExportCategoryFragmentV1 Category(string category, int count, ExportArtifactReceipt receipt) =>
        new(category, count == 0 ? ExportCategoryDispositionV1.Empty : ExportCategoryDispositionV1.Included,
            count, "admin-area.fixture.v1", receipt.ContentSha256, receipt.ArtifactReference, null);

    private async Task<bool> DemandInboxReplayAsync(LifecycleMessageHeaderV1 header, string commandType,
        string requestHash, CancellationToken cancellationToken)
    {
        var existing = await database.AdminAreaExportInbox.AsNoTracking().SingleOrDefaultAsync(
            x => x.MessageId == header.MessageId, cancellationToken);
        if (existing is null) return false;
        if (existing.OperationId != header.OperationId || existing.CommandType != commandType
            || !StringComparer.Ordinal.Equals(existing.RequestHash, requestHash))
            throw new InvalidOperationException("A lifecycle message identity was reused for different content.");
        return true;
    }

    private async Task RecordInboxAndOutboxAsync<T>(LifecycleMessageHeaderV1 header, string commandType,
        string requestHash, T response, CancellationToken cancellationToken)
    {
        var occurredAt = UtcMicrosecond(clock.GetUtcNow());
        database.AdminAreaExportInbox.Add(new AdminAreaExportInbox(header.MessageId, header.OrganisationId,
            header.OperationId, commandType, requestHash, occurredAt));
        database.AdminAreaExportOutbox.Add(new AdminAreaExportOutbox(
            DeterministicMessageId(header.MessageId, response!.GetType().FullName!), header.OrganisationId,
            header.OperationId, response.GetType().FullName!, JsonSerializer.Serialize(response, Json), occurredAt));
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task AcquireOrganisationLockAsync(Guid organisationId, CancellationToken cancellationToken)
    {
        if (!attempt.IsActive || attempt.OrganisationId != organisationId)
            throw new InvalidOperationException("The export advisory lock requires an initialized tenant transaction.");
        var transaction = database.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("The export advisory lock requires an active database transaction.");
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtextextended(@organisation, @seed))";
        command.Parameters.Add(new NpgsqlParameter("organisation", organisationId.ToString("D")));
        command.Parameters.Add(new NpgsqlParameter<long>("seed", AdvisoryLockSeed));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static OrganisationExportFenceEnteredV1 FenceReceipt(
        LifecycleMessageHeaderV1 header, AdminAreaExportFence fence) =>
        new(header, fence.FenceToken, fence.FenceRevision, fence.EnteredAt);

    private static OrganisationExportFenceReleasedV1 Released(
        LifecycleMessageHeaderV1 header, AdminAreaExportFence fence) =>
        new(header, fence.FenceToken, fence.ReleasedAt
            ?? throw new InvalidOperationException("The fence has not been released."));

    private static OrganisationExportFragmentReadyV1 DeserializeFragment(
        LifecycleMessageHeaderV1 header, AdminAreaExportFragment fragment)
    {
        using var document = JsonDocument.Parse(fragment.PayloadJson);
        var categories = document.RootElement.GetProperty("categories").EnumerateArray().Select(category =>
            new ExportCategoryFragmentV1(
                category.GetProperty("category").GetString()!,
                (ExportCategoryDispositionV1)category.GetProperty("disposition").GetInt32(),
                category.GetProperty("recordCount").GetInt64(),
                OptionalString(category, "schemaVersion"),
                OptionalString(category, "contentSha256"),
                OptionalString(category, "artifactReference"),
                OptionalString(category, "reasonCode"))).ToArray();
        var response = new OrganisationExportFragmentReadyV1(
            header, fragment.SnapshotAt, fragment.FenceToken, categories);
        if (!StringComparer.Ordinal.Equals(response.FragmentHash, fragment.FragmentHash))
            throw new InvalidDataException("The durable export fragment hash does not match its payload.");
        return response;
    }

    private static string? OptionalString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetString();
    }

    private static void DemandHeader(LifecycleMessageHeaderV1 header, string participant)
    {
        if (header.ParticipantId != participant || header.ContractVersion != LifecycleContractV1.Version)
            throw new InvalidOperationException("The lifecycle header does not match the AdminArea V1 contract.");
    }

    private static string DeterministicToken(LifecycleMessageHeaderV1 header, long revision) =>
        "admin-area-" + CanonicalHash("admin-area-fence-token-v1", header.OperationId,
            header.OrganisationId, header.OperationRevision, revision)[..32];

    private static string RequestHash(string command, LifecycleMessageHeaderV1 header, params object[] values)
    {
        return CanonicalHash(new object?[]
        {
            command, header.OperationId, header.OrganisationId, header.OperationRevision,
            header.ParticipantId, header.ContractVersion, header.MessageId, header.CausationId,
            header.CorrelationId
        }.Concat(values).ToArray());
    }

    private static string CanonicalHash(params object?[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values) AppendCanonical(hash, value);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendCanonical(IncrementalHash hash, object? value)
    {
        var canonical = value switch
        {
            null => null,
            Guid guid => guid.ToString("D", CultureInfo.InvariantCulture),
            DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTime timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()
        };
        if (canonical is null)
        {
            Span<byte> missing = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(missing, -1);
            hash.AppendData(missing);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(canonical);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static Guid DeterministicMessageId(Guid messageId, string responseType)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{messageId:D}\n{responseType}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static DateTimeOffset UtcMicrosecond(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    private sealed class BinaryWriterStream(Stream stream) : IAsyncDisposable
    {
        public async Task WriteInt32Async(int value, CancellationToken token) =>
            await stream.WriteAsync(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(value)), token);

        public async Task WriteBytesAsync(byte[] value, CancellationToken token)
        {
            await WriteInt32Async(value.Length, token);
            await stream.WriteAsync(value, token);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record MaterializedCategory(
        ExportCategoryFragmentV1 Category,
        ExportArtifactReceipt Receipt);
}
