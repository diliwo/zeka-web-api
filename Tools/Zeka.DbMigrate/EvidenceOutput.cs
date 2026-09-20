using System.Text.Json;

namespace Zeka.DbMigrate;

internal sealed class EvidenceOutput
{
    private readonly string finalPath;
    private readonly string directory;

    private EvidenceOutput(string finalPath)
    {
        this.finalPath = finalPath;
        directory = Path.GetDirectoryName(finalPath)!;
    }

    public static EvidenceOutput Create(string authorizedRoot, string relativeFile)
    {
        if (!Path.IsPathFullyQualified(authorizedRoot) || string.IsNullOrWhiteSpace(relativeFile)
            || Path.IsPathRooted(relativeFile) || relativeFile.IndexOf(':') >= 0
            || relativeFile.StartsWith('\\') || relativeFile.Contains('\0'))
            throw new EvidencePathException();
        var root = Path.GetFullPath(authorizedRoot);
        RejectLink(new DirectoryInfo(root));
        if (!Directory.Exists(root)) throw new EvidencePathException();
        var components = relativeFile.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.None);
        if (components.Length == 0 || components.Any(x => x is "" or "." or ".."))
            throw new EvidencePathException();
        var final = Path.GetFullPath(Path.Combine(root, relativeFile));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!final.StartsWith(prefix, StringComparison.Ordinal) || final == root) throw new EvidencePathException();
        var parent = root;
        foreach (var component in components[..^1])
        {
            parent = Path.Combine(parent, component);
            RejectLink(new DirectoryInfo(parent));
            if (!Directory.Exists(parent)) throw new EvidencePathException();
        }
        RejectLink(new FileInfo(final));
        if (File.Exists(final) || Directory.Exists(final)) throw new EvidencePathException();
        return new EvidenceOutput(final);
    }

    public async Task PublishAsync(MigrationEvidence evidence, CancellationToken cancellationToken = default)
    {
        var temporary = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, evidence,
                    new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, finalPath, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RejectLink(FileSystemInfo item)
    {
        item.Refresh();
        if (item.LinkTarget is not null || item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new EvidencePathException();
    }
}

internal sealed class EvidencePathException : Exception;
