using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Zeka.Lifecycle.Contracts;

namespace ClientManagement.Application.Lifecycle;

public static class ClientExportContract
{
    public const string ParticipantId = "client-management";
    public const string FenceCapability = "export-fence";
    public const string FragmentCapability = "client-management.export-fragment";
    public static readonly string[] Categories =
    [
        "audit-events", "beneficiaries", "beneficiary-assignments", "case-histories",
        "generated-report-artifacts", "integration-events", "notes", "professional-history",
        "school-history", "structured-assessments", "structured-reports"
    ];
}

public interface IClientExportArtifactStore
{
    Task<ClientExportArtifactReceipt> WriteVerifiedAsync(Guid organisationId, Guid operationId,
        ClientExportArtifactWrite artifact, CancellationToken cancellationToken);
    Task VerifyExactAsync(Guid organisationId, Guid operationId, string setName,
        IReadOnlyCollection<ClientExportArtifactReceipt> expected, CancellationToken cancellationToken);
}

public sealed record ClientExportArtifactWrite(string SetName, string RelativeName, ReadOnlyMemory<byte> Content);
public sealed record ClientExportArtifactReceipt(string ArtifactReference, string ContentSha256, long Length);

public interface IClientExportFixtureScope
{
    void Demand(Guid organisationId);
}

public interface IClientOrganisationExportParticipant
{
    Task<OrganisationExportFenceEnteredV1> EnterFenceAsync(EnterOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default);
    Task<OrganisationExportFragmentReadyV1> StageAsync(StageOrganisationExportV1 command,
        CancellationToken cancellationToken = default);
    Task<OrganisationExportFenceReleasedV1> ReleaseFenceAsync(ReleaseOrganisationExportFenceV1 command,
        CancellationToken cancellationToken = default);
}

public static class ClientExportCanonical
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Sha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    public static string RequestHash<T>(T value) => Sha256(JsonSerializer.SerializeToUtf8Bytes(value, Json));

    public static byte[] Csv(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var builder = new StringBuilder();
        Write(headers);
        foreach (var row in rows) Write(row);
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(builder.ToString());

        void Write(IReadOnlyList<string?> fields)
        {
            if (fields.Count != headers.Count) throw new InvalidOperationException("CSV row width is invalid.");
            for (var index = 0; index < fields.Count; index++)
            {
                if (index != 0) builder.Append(',');
                var value = fields[index] ?? string.Empty;
                if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
                    builder.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                else builder.Append(value);
            }
            builder.Append('\n');
        }
    }
}
