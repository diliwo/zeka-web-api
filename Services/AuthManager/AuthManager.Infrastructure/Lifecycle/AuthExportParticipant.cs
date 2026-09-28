using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Lifecycle;

/// <summary>AuthManagement-owned LIFE-01 participant. Production transport registration is intentionally absent.</summary>
public sealed class AuthExportParticipant(
    DbContextOptions<AuthDbContext> options,
    IExportArtifactSink artifacts)
{
    public async Task<OrganisationExportFragmentReadyV1> StageAsync(
        LifecycleMessageHeaderV1 header,
        DateTimeOffset snapshotAt,
        string fenceToken,
        CancellationToken cancellationToken = default)
    {
        if (header.ParticipantId != AuthExportInventoryV1.ParticipantId)
            throw new InvalidOperationException("Auth export participant identity is required.");
        await using var database = new AuthDbContext(options);
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_catalog.set_config('zeka.organisation_id', {header.OrganisationId.ToString("D")}, true)",
            cancellationToken);

        var memberships = await database.OrganisationMemberships.AsNoTracking()
            .Where(x => x.OrganisationId == header.OrganisationId && x.JoinedAtUtc <= snapshotAt)
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
        var contentHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        var reference = $"fragments/{header.ParticipantId}/{AuthExportInventoryV1.Memberships}.csv";
        await artifacts.StoreAsync(reference, contentHash, content, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new OrganisationExportFragmentReadyV1(header, snapshotAt, fenceToken,
        [
            new ExportCategoryFragmentV1(AuthExportInventoryV1.Memberships,
                memberships.Count == 0 ? ExportCategoryDispositionV1.Empty : ExportCategoryDispositionV1.Included,
                memberships.Count, "auth-memberships-v1", contentHash, reference, null),
            new ExportCategoryFragmentV1(AuthExportInventoryV1.AuditEvents,
                ExportCategoryDispositionV1.Withheld, 0, null, null, null, "disclosure-policy-unresolved"),
            new ExportCategoryFragmentV1(AuthExportInventoryV1.IntegrationEvents,
                ExportCategoryDispositionV1.Withheld, 0, null, null, null, "raw-outbox-export-prohibited")
        ]);
    }

    private static string Format(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
}
