using System.Security.Cryptography;
using AdminAreaManagement.Application.Exports;

namespace AdminAreaManagement.Infrastructure.Exports;

/// <summary>
/// Non-production, content-addressed artifact adapter used by LIFE-01 provider-real evidence.
/// Every operation is confined to one configured root and verified after durable replacement.
/// </summary>
public sealed class DeterministicFixtureExportArtifactStore : IAdminAreaExportArtifactStore
{
    private readonly string root;

    public DeterministicFixtureExportArtifactStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("A fixture artifact root is required.", nameof(rootPath));
        root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(root);
        RejectLink(new DirectoryInfo(root));
        root = (new DirectoryInfo(root).ResolveLinkTarget(true) ?? new DirectoryInfo(root)).FullName;
    }

    public async Task<ExportArtifactReceipt> WriteVerifiedAsync(Guid organisationId, Guid operationId,
        ExportArtifactWrite artifact, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(organisationId, operationId);
        ValidateSegment(artifact.SetName, "Artifact set");
        if (string.IsNullOrWhiteSpace(artifact.RelativeName)
            || artifact.RelativeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || artifact.RelativeName.Contains("..", StringComparison.Ordinal)
            || artifact.RelativeName.Contains(Path.DirectorySeparatorChar)
            || artifact.RelativeName.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException("Artifact name is not a single safe path segment.");

        var hash = Convert.ToHexString(SHA256.HashData(artifact.Content.Span)).ToLowerInvariant();
        var operationDirectory = ArtifactSetDirectory(organisationId, operationId, artifact.SetName, create: true);
        var fileName = $"{artifact.RelativeName}.{hash}.artifact";
        var destination = Contained(operationDirectory, fileName);
        RejectLink(new FileInfo(destination));

        if (File.Exists(destination))
        {
            var existing = await File.ReadAllBytesAsync(destination, cancellationToken);
            if (!existing.AsSpan().SequenceEqual(artifact.Content.Span))
                throw new InvalidDataException("A content-addressed artifact was tampered with.");
        }
        else
        {
            var temporary = Contained(operationDirectory, $".{fileName}.{Guid.NewGuid():N}.pending");
            try
            {
                await using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(artifact.Content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        var observed = await File.ReadAllBytesAsync(destination, cancellationToken);
        if (!SHA256.HashData(observed).AsSpan().SequenceEqual(Convert.FromHexString(hash)))
            throw new InvalidDataException("Artifact verification failed after durable write.");
        var reference = $"admin-area-fixture/{organisationId:N}/{operationId:N}/{artifact.SetName}/{fileName}";
        return new ExportArtifactReceipt(reference, hash, observed.LongLength);
    }

    public async Task VerifyExactAsync(Guid organisationId, Guid operationId,
        string setName, IReadOnlyCollection<ExportArtifactReceipt> expected,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(organisationId, operationId);
        ArgumentNullException.ThrowIfNull(expected);
        ValidateSegment(setName, "Artifact set");
        var directory = ArtifactSetDirectory(organisationId, operationId, setName, create: false);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Export artifact directory is missing.");
        RejectLink(new DirectoryInfo(directory));

        var expectedFiles = expected.Select(x => Path.GetFileName(x.ArtifactReference))
            .Order(StringComparer.Ordinal).ToArray();
        if (expectedFiles.Distinct(StringComparer.Ordinal).Count() != expectedFiles.Length)
            throw new InvalidDataException("Expected artifact identities are not unique.");
        var observedFiles = Directory.EnumerateFileSystemEntries(directory).Select(path =>
        {
            var info = new FileInfo(path);
            RejectLink(info);
            if (!info.Exists) throw new InvalidDataException("Artifact directory contains a non-file entry.");
            return info.Name;
        }).Order(StringComparer.Ordinal).ToArray();
        if (!expectedFiles.SequenceEqual(observedFiles, StringComparer.Ordinal))
            throw new InvalidDataException("Artifact directory contains missing or extra entries.");

        foreach (var receipt in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Contained(directory, Path.GetFileName(receipt.ArtifactReference));
            var content = await File.ReadAllBytesAsync(path, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!StringComparer.Ordinal.Equals(hash, receipt.ContentSha256) || content.LongLength != receipt.Length)
                throw new InvalidDataException("An export artifact is missing, truncated or tampered with.");
        }
    }

    private string OperationDirectory(Guid organisationId, Guid operationId, bool create)
    {
        var organisation = Contained(root, organisationId.ToString("N"));
        if (create) Directory.CreateDirectory(organisation);
        RejectLink(new DirectoryInfo(organisation));
        var operation = Contained(organisation, operationId.ToString("N"));
        if (create) Directory.CreateDirectory(operation);
        RejectLink(new DirectoryInfo(operation));
        return operation;
    }

    private string ArtifactSetDirectory(Guid organisationId, Guid operationId, string setName, bool create)
    {
        var operation = OperationDirectory(organisationId, operationId, create);
        var set = Contained(operation, setName);
        if (create) Directory.CreateDirectory(set);
        RejectLink(new DirectoryInfo(set));
        return set;
    }

    private static string Contained(string parent, string child)
    {
        var result = Path.GetFullPath(Path.Combine(parent, child));
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!result.StartsWith(prefix, comparison))
            throw new InvalidDataException("Artifact path escaped its configured boundary.");
        return result;
    }

    private static void RejectLink(FileSystemInfo info)
    {
        if (info.Exists && (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("Artifact storage cannot traverse symbolic links or reparse points.");
    }

    private static void ValidateIdentity(Guid organisationId, Guid operationId)
    {
        if (organisationId == Guid.Empty || operationId == Guid.Empty)
            throw new ArgumentException("A complete export artifact identity is required.");
    }

    private static void ValidateSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains("..", StringComparison.Ordinal)
            || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw new InvalidDataException($"{description} is not a single safe path segment.");
    }
}
