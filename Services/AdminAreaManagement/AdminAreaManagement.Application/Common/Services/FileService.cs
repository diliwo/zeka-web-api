using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Application.Common.Services
{
    public class FileService : IFileService
    {
        private readonly string _storageRoot;

        public FileService(FileRepositorySettings settings)
            : this(settings?.FileServerPath ?? throw new ArgumentNullException(nameof(settings)))
        {
        }

        public FileService(string fileServerPath)
        {
            if (string.IsNullOrWhiteSpace(fileServerPath))
                throw new InvalidOperationException("FileServerPath is required.");
            var configuredRoot = Path.GetFullPath(fileServerPath);
            Directory.CreateDirectory(configuredRoot);
            var root = new DirectoryInfo(configuredRoot);
            _storageRoot = (root.ResolveLinkTarget(returnFinalTarget: true) ?? root).FullName;
        }

        public byte[] GetContentFile(Guid organisationId, int partnerId, int docId)
        {
            return File.ReadAllBytes(DocumentPath(organisationId, partnerId, docId));
        }

        public string GetFolderPath(Guid organisationId, int partnerId)
        {
            ValidateIdentity(organisationId, partnerId, 1);
            return DocumentFolder(organisationId, partnerId);
        }

        public void DeleteFile(Guid organisationId, int id, int partnerId)
        {
            var path = DocumentPath(organisationId, partnerId, id);
            if (File.Exists(path)) File.Delete(path);
        }

        public void SaveFile(Guid organisationId, int id, int partnerId, byte[] contentFile)
        {
            ArgumentNullException.ThrowIfNull(contentFile);
            if (contentFile.Length == 0) throw new InvalidDataException("Document content is required.");
            var folderPath = DocumentFolder(organisationId, partnerId);
            Directory.CreateDirectory(folderPath);
            RejectDirectoryLink(folderPath);
            var fileFullPath = DocumentPath(organisationId, partnerId, id);
            if (File.Exists(fileFullPath))
            {
                if (File.ReadAllBytes(fileFullPath).AsSpan().SequenceEqual(contentFile)) return;
                throw new IOException("A different document already exists for this storage identity.");
            }
            var temporaryPath = ContainedPath(organisationId.ToString("N"), partnerId.ToString("D10"),
                $".{id:D10}.{Guid.NewGuid():N}.pending");
            try
            {
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           81920, FileOptions.WriteThrough))
                {
                    stream.Write(contentFile);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporaryPath, fileFullPath, overwrite: false);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private string DocumentPath(Guid organisationId, int partnerId, int documentId)
        {
            ValidateIdentity(organisationId, partnerId, documentId);
            var folder = DocumentFolder(organisationId, partnerId);
            return ContainedPath(Path.GetRelativePath(_storageRoot, folder),
                $"document-{documentId:D10}.bin");
        }

        private string DocumentFolder(Guid organisationId, int partnerId)
        {
            var organisation = ContainedPath(organisationId.ToString("N"));
            RejectDirectoryLink(organisation);
            var folder = ContainedPath(organisationId.ToString("N"), partnerId.ToString("D10"));
            RejectDirectoryLink(folder);
            return folder;
        }

        private string ContainedPath(params string[] segments)
        {
            var path = Path.GetFullPath(Path.Combine([_storageRoot, .. segments]));
            var rootPrefix = _storageRoot.EndsWith(Path.DirectorySeparatorChar)
                ? _storageRoot : _storageRoot + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(rootPrefix, comparison))
                throw new InvalidOperationException("Document path escaped the configured storage root.");
            return path;
        }

        private static void RejectDirectoryLink(string path)
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null)
                throw new InvalidOperationException("Document storage cannot traverse a symbolic link.");
        }

        private static void ValidateIdentity(Guid organisationId, int partnerId, int documentId)
        {
            if (organisationId == Guid.Empty || partnerId <= 0 || documentId <= 0)
                throw new InvalidOperationException("Document storage identity is invalid.");
        }
    }

    public class FileRepositorySettings
    {
        public string FileServerPath { get; private set; }

        public FileRepositorySettings(string fileServerPath)
        {
            if (string.IsNullOrWhiteSpace(fileServerPath))
                throw new InvalidOperationException("FileServerPath is required.");
            FileServerPath = fileServerPath;
        }
    }
}