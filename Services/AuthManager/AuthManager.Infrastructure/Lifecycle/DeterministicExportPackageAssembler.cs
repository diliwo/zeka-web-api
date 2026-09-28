using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Lifecycle;

namespace AuthManager.Infrastructure.Lifecycle;

public sealed class DeterministicExportPackageAssembler : IExportPackageAssembler
{
    private static readonly DateTimeOffset ZipEpoch = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public ExportPackageOutput Assemble(ExportPackageInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.OperationId == Guid.Empty || input.OrganisationId == Guid.Empty || input.RegistryRevision == Guid.Empty)
            throw new ArgumentException("Export package identity is incomplete.", nameof(input));
        if (input.SnapshotAt == default || input.SnapshotAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("SnapshotAt must be UTC.", nameof(input));

        var categories = input.Categories
            .OrderBy(x => x.ParticipantId, StringComparer.Ordinal)
            .ThenBy(x => x.Category, StringComparer.Ordinal)
            .ToArray();
        if (categories.Length == 0
            || categories.DistinctBy(x => (x.ParticipantId, x.Category)).Count() != categories.Length)
            throw new InvalidOperationException("Every frozen export category must be accounted for exactly once.");

        var materialized = categories.Where(x => x.Disposition is "included" or "empty").ToArray();
        foreach (var category in materialized)
        {
            if (string.IsNullOrWhiteSpace(category.ArtifactReference)
                || !input.Artifacts.TryGetValue(category.ArtifactReference, out var artifactBytes)
                || !string.Equals(Hash(artifactBytes), category.ContentSha256, StringComparison.Ordinal))
                throw new InvalidOperationException("Materialized category bytes must match their declared artifact and hash.");
        }
        if (input.Artifacts.Keys.Except(materialized.Select(x => x.ArtifactReference!), StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Unreferenced export artifacts are not permitted.");

        var manifestModel = new
        {
            schemaVersion = "zeka-organisation-export-manifest-v1",
            operationId = input.OperationId,
            organisationId = input.OrganisationId,
            registryRevision = input.RegistryRevision,
            inventoryHash = input.InventoryHash,
            snapshotAt = input.SnapshotAt,
            fenceEvidenceHash = input.FenceEvidenceHash,
            categories = categories.Select(x => new
            {
                participantId = x.ParticipantId,
                category = x.Category,
                disposition = x.Disposition,
                recordCount = x.RecordCount,
                schemaVersion = x.SchemaVersion,
                contentSha256 = x.ContentSha256,
                artifactReference = x.ArtifactReference,
                reasonCode = x.ReasonCode
            })
        };
        var manifest = JsonSerializer.SerializeToUtf8Bytes(manifestModel, Json);
        var manifestHash = Hash(manifest);

        using var destination = new MemoryStream();
        using (var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(archive, "manifest.json", manifest);
            foreach (var artifact in input.Artifacts.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                ValidatePath(artifact.Key);
                Write(archive, artifact.Key, artifact.Value);
            }
        }
        var content = destination.ToArray();
        return new(content, manifest, manifestHash, Hash(content));
    }

    private static void Write(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        entry.LastWriteTime = ZipEpoch;
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\')
            || path.Contains("..", StringComparison.Ordinal) || path.Contains('\\'))
            throw new InvalidOperationException("Artifact references must be safe relative ZIP paths.");
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}

/// <summary>Deterministic in-memory sink for provider-real non-production evidence only.</summary>
public sealed class InMemoryExportPackageSink : IExportPackageSink
{
    private readonly Dictionary<Guid, (string Hash, byte[] Content)> packages = [];

    public Task<string> StoreAsync(Guid operationId, string packageSha256, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.ToArray();
        if (packages.TryGetValue(operationId, out var existing)
            && (!string.Equals(existing.Hash, packageSha256, StringComparison.Ordinal)
                || !existing.Content.AsSpan().SequenceEqual(bytes)))
            throw new InvalidOperationException("An immutable operation package cannot be replaced.");
        packages[operationId] = (packageSha256, bytes);
        return Task.FromResult($"fixture/packages/{operationId:D}.zip");
    }

    public ReadOnlyMemory<byte> Read(Guid operationId) => packages[operationId].Content;
}

public sealed class InMemoryExportArtifactStore : IExportArtifactSource, IExportArtifactSink
{
    private readonly Dictionary<string, (string Hash, byte[] Content)> artifacts = new(StringComparer.Ordinal);

    public Task<string> StoreAsync(string artifactReference, string contentSha256,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = content.ToArray();
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actual, contentSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact bytes do not match the declared hash.");
        if (artifacts.TryGetValue(artifactReference, out var existing)
            && (!string.Equals(existing.Hash, contentSha256, StringComparison.Ordinal)
                || !existing.Content.AsSpan().SequenceEqual(bytes)))
            throw new InvalidOperationException("An immutable artifact reference cannot be replaced.");
        artifacts[artifactReference] = (contentSha256, bytes);
        return Task.FromResult(artifactReference);
    }

    public Task<ReadOnlyMemory<byte>> ReadAsync(string artifactReference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<ReadOnlyMemory<byte>>(artifacts[artifactReference].Content);
    }
}

/// <summary>Production-safe default: storage remains unavailable until a separately accepted provider is configured.</summary>
public sealed class UnconfiguredExportStorage : IExportArtifactSource, IExportArtifactSink, IExportPackageSink
{
    private const string Message = "Organisation export storage is not configured; LIFE-01 production activation is disabled.";

    public Task<ReadOnlyMemory<byte>> ReadAsync(string artifactReference, CancellationToken cancellationToken) =>
        Task.FromException<ReadOnlyMemory<byte>>(new InvalidOperationException(Message));

    public Task<string> StoreAsync(string artifactReference, string contentSha256,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        Task.FromException<string>(new InvalidOperationException(Message));

    public Task<string> StoreAsync(Guid operationId, string packageSha256,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
        Task.FromException<string>(new InvalidOperationException(Message));
}
