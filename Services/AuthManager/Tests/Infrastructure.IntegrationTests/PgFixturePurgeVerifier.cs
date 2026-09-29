using System.Data;
using System.Security.Cryptography;
using System.Text;
using AuthManager.Application.Lifecycle;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

/// <summary>Read-only owner-local postcondition adapter. It never calls the destructive participant.</summary>
internal sealed class PgFixturePurgeVerifier(string readOnlyConnectionString, string participantId,
    TimeProvider clock, string? documentRoot = null, string? documentPath = null)
    : INonProductionPurgeVerifier
{
    public string ParticipantId => participantId;

    public async Task<VerifyPurgeReceiptV1> ObserveAsync(VerifyPurgeCommandV1 command,
        CancellationToken cancellationToken)
    {
        command.Validate();
        if (command.ParticipantId != participantId || clock.GetUtcNow() >= command.ExpiresAt)
            throw new InvalidOperationException("Verifier identity or command freshness failed.");
        await using var connection = new NpgsqlConnection(readOnlyConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await using (var context = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @org, true)",
            connection, transaction))
        {
            context.Parameters.AddWithValue("org", command.OrganisationId.ToString("D"));
            if (!Equals(await context.ExecuteScalarAsync(cancellationToken),
                    command.OrganisationId.ToString("D")))
                throw new InvalidOperationException("Verifier tenant context failed.");
        }
        string? disposition;
        await using (var item = new NpgsqlCommand("""
            SELECT "Disposition" FROM life04a_fixture."Items"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "Category"=@category AND "ItemId"=@item
            """, connection, transaction))
        {
            Bind(item, command);
            disposition = await item.ExecuteScalarAsync(cancellationToken) as string;
        }
        // A missing or changed frozen fixture obligation is incomplete evidence, never absence.
        if (disposition != command.ExpectedDisposition)
            throw new InvalidOperationException("Frozen owner-local fixture obligation is missing.");
        long metadataCount;
        await using (var metadata = new NpgsqlCommand("""
            SELECT count(*) FROM life04a_fixture."Payloads"
            WHERE "OrganisationId"=@org AND "ParticipantId"=@participant
              AND "Category"=@category AND "ItemId"=@item
            """, connection, transaction))
        {
            Bind(metadata, command);
            metadataCount = Convert.ToInt64(await metadata.ExecuteScalarAsync(cancellationToken));
        }
        int? fileResidual = null;
        if (participantId == "admin-area-documents"
            && command.Category == "admin-area-document-storage")
            fileResidual = ObserveDocumentFile(command);
        else if (documentPath is not null || documentRoot is not null)
            throw new InvalidOperationException("Document storage bound to wrong fixture owner.");
        var now = clock.GetUtcNow();
        if (now >= command.ExpiresAt)
            throw new InvalidOperationException("Verification observation expired.");
        await transaction.CommitAsync(cancellationToken);
        var residual = disposition == "PURGE" ? checked((int)metadataCount) : 0;
        var retained = disposition == "RETAIN" ? checked((int)metadataCount) : 0;
        var expectedRetained = disposition == "RETAIN" ? 1 : 0;
        int? fileExpected = fileResidual is null ? null : disposition == "RETAIN" ? 1 : 0;
        var fileFailure = fileResidual is not null && (disposition == "PURGE"
            ? fileResidual != 0 : fileResidual != 1);
        var satisfied = residual == 0 && retained == expectedRetained && !fileFailure;
        var proof = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"zeka-owner-observation-v1\n{command.Hash()}\n{now:O}\n{disposition}\n{residual}\n{retained}\n{expectedRetained}\n{fileResidual}\n{fileExpected}"))).ToLowerInvariant();
        return new VerifyPurgeReceiptV1(Guid.NewGuid(), command.MessageId,
            command.Hash(), command.OrganisationId, command.TerminationOperationId,
            command.IrreversibleRevision, command.RegistryRevision, command.InventoryHash,
            command.DecisionSetId, command.DecisionSetHash, command.PlanId, command.PlanHash,
            participantId, command.CapabilityKey, command.Category, command.ItemId,
            "synthetic-pg-owner-query-v1", now, command.ExpiresAt, residual,
            retained, expectedRetained, fileResidual, fileExpected, satisfied, proof);
    }

    private int ObserveDocumentFile(VerifyPurgeCommandV1 command)
    {
        if (documentRoot is null || documentPath is null)
            throw new InvalidOperationException("Document verifier requires isolated root and path.");
        var root = Path.GetFullPath(documentRoot);
        var file = Path.GetFullPath(documentPath);
        var itemDigest = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(command.ItemId))).ToLowerInvariant();
        var expected = Path.GetFullPath(Path.Combine(root,
            command.OrganisationId.ToString("N"),
            command.TerminationOperationId.ToString("N"), itemDigest + ".bin"));
        if (!string.Equals(file, expected, StringComparison.Ordinal)
            || !file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !Directory.Exists(root))
            throw new InvalidOperationException("Document verification path conflicts with frozen tenant, operation or item.");
        for (var current = Path.GetDirectoryName(file); current is not null
             && current.Length >= root.Length; current = Path.GetDirectoryName(current))
        {
            if (new DirectoryInfo(current).LinkTarget is not null)
                throw new InvalidOperationException("Document verification path contains a symbolic link.");
            if (current == root) break;
        }
        try
        {
            if (new FileInfo(file).LinkTarget is not null)
                throw new InvalidOperationException("Document verification file is a symbolic link.");
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return 1;
        }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private static void Bind(NpgsqlCommand query, VerifyPurgeCommandV1 command)
    {
        query.Parameters.AddWithValue("org", command.OrganisationId);
        query.Parameters.AddWithValue("participant", command.ParticipantId);
        query.Parameters.AddWithValue("category", command.Category);
        query.Parameters.AddWithValue("item", command.ItemId);
    }
}
