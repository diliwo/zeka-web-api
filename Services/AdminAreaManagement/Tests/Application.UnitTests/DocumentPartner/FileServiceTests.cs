using AdminAreaManagement.Application.Common.Services;
using Xunit;

namespace Application.UnitTests.DocumentPartner;

public sealed class FileServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"zeka-document-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Canonical_storage_is_contained_idempotent_and_ignores_untrusted_metadata()
    {
        var service = new FileService(root);
        var organisation = Guid.NewGuid();
        byte[] content = [1, 2, 3, 4];

        service.SaveFile(organisation, 7, 11, content);
        service.SaveFile(organisation, 7, 11, content);

        Assert.Equal(content, service.GetContentFile(organisation, 11, 7));
        var expected = Path.Combine(root, organisation.ToString("N"), "0000000011", "document-0000000007.bin");
        Assert.True(File.Exists(expected));
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, Path.GetFullPath(expected));

        service.DeleteFile(organisation, 7, 11);
        service.DeleteFile(organisation, 7, 11);
        Assert.False(File.Exists(expected));
    }

    [Fact]
    public void Existing_different_content_is_rejected_without_partial_overwrite()
    {
        var service = new FileService(root);
        var organisation = Guid.NewGuid();
        byte[] original = [1, 2, 3];
        service.SaveFile(organisation, 3, 5, original);

        Assert.Throws<IOException>(() => service.SaveFile(organisation, 3, 5, [9, 8, 7]));

        Assert.Equal(original, service.GetContentFile(organisation, 5, 3));
        Assert.Empty(Directory.EnumerateFiles(service.GetFolderPath(organisation, 5), "*.pending"));
    }

    [Fact]
    public void Symbolic_link_escape_is_rejected()
    {
        var service = new FileService(root);
        var organisation = Guid.NewGuid();
        var outside = Path.Combine(Path.GetTempPath(), $"zeka-document-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, organisation.ToString("N")), outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() => service.SaveFile(organisation, 1, 1, [1]));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Existing_final_file_symbolic_link_is_rejected_without_accessing_external_target()
    {
        var service = new FileService(root);
        var organisation = Guid.NewGuid();
        const int documentId = 17;
        const int partnerId = 19;
        var folder = service.GetFolderPath(organisation, partnerId);
        Directory.CreateDirectory(folder);
        var documentPath = Path.Combine(folder, $"document-{documentId:D10}.bin");
        var outside = Path.Combine(Path.GetTempPath(), $"zeka-document-outside-{Guid.NewGuid():N}.bin");
        byte[] externalContent = [4, 3, 2, 1];
        File.WriteAllBytes(outside, externalContent);
        File.CreateSymbolicLink(documentPath, outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                service.GetContentFile(organisation, partnerId, documentId));
            Assert.Throws<InvalidOperationException>(() =>
                service.SaveFile(organisation, documentId, partnerId, [9, 9, 9]));
            Assert.Throws<InvalidOperationException>(() =>
                service.DeleteFile(organisation, documentId, partnerId));

            Assert.True(File.Exists(outside));
            Assert.Equal(externalContent, File.ReadAllBytes(outside));
            Assert.NotNull(new FileInfo(documentPath).LinkTarget);
        }
        finally
        {
            File.Delete(documentPath);
            File.Delete(outside);
        }
    }

    [Fact]
    public void Dangling_final_file_symbolic_link_is_rejected_without_creating_external_target()
    {
        var service = new FileService(root);
        var organisation = Guid.NewGuid();
        const int documentId = 23;
        const int partnerId = 29;
        var folder = service.GetFolderPath(organisation, partnerId);
        Directory.CreateDirectory(folder);
        var documentPath = Path.Combine(folder, $"document-{documentId:D10}.bin");
        var outside = Path.Combine(Path.GetTempPath(), $"zeka-document-missing-{Guid.NewGuid():N}.bin");
        File.CreateSymbolicLink(documentPath, outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                service.GetContentFile(organisation, partnerId, documentId));
            Assert.Throws<InvalidOperationException>(() =>
                service.SaveFile(organisation, documentId, partnerId, [1, 2, 3]));
            Assert.Throws<InvalidOperationException>(() =>
                service.DeleteFile(organisation, documentId, partnerId));

            Assert.False(File.Exists(outside));
            Assert.NotNull(new FileInfo(documentPath).LinkTarget);
        }
        finally
        {
            File.Delete(documentPath);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    public void Invalid_storage_identity_is_rejected(int partnerId, int documentId)
    {
        var service = new FileService(root);
        Assert.Throws<InvalidOperationException>(() =>
            service.SaveFile(Guid.NewGuid(), documentId, partnerId, [1]));
        Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_storage_root_fails_closed(string? configuredRoot)
    {
        Assert.Throws<InvalidOperationException>(() => new FileService(configuredRoot!));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
