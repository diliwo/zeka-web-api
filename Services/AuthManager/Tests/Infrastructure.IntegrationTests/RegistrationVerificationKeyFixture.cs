using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SystemPath = System.IO.Path;

namespace Infrastructure.IntegrationTests;

// Synthetic verification HMAC material only. This never supplies or rotates JWT keys.
// An explicit owner path is borrowed unchanged; only an absent input enables generation.
internal sealed class RegistrationVerificationKeyFixture : IDisposable
{
    private readonly string? ownedDirectory;
    internal string Path { get; }
    private bool disposed;

    private RegistrationVerificationKeyFixture(string path, string? directory)
    { Path = path; ownedDirectory = directory; }

    internal static RegistrationVerificationKeyFixture Create(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            ValidatePrivateFile(explicitPath);
            return new(explicitPath, null);
        }

        var directory = SystemPath.Combine(SystemPath.GetTempPath(), "zeka-test-verification-" + Guid.NewGuid().ToString("N"));
        var path = SystemPath.Combine(directory, "verification-hmac.bin");
        if (Directory.Exists(directory) || File.Exists(directory)) throw Rejected();
        try
        {
            if (OperatingSystem.IsWindows()) CreatePrivateWindowsDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw Rejected();

            var material = RandomNumberGenerator.GetBytes(32);
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(path, options);
                stream.Write(material);
                stream.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(material); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead);
            ValidatePrivateFile(path);
            return new(path, directory);
        }
        catch
        {
            // Exact newly owned paths only; no recursive removal or borrowed-file cleanup.
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
            throw;
        }
    }

    private static void ValidatePrivateFile(string path)
    {
        if (!SystemPath.IsPathFullyQualified(path) || !File.Exists(path)
            || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0
            || new FileInfo(path).Length != 32) throw Rejected();
        if (OperatingSystem.IsWindows()) ValidateWindowsFile(path);
        else if ((File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0) throw Rejected();
    }

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw Rejected();
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw Rejected();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { owner.Value, "S-1-5-18", "S-1-5-32-544" };
        var security = new FileInfo(path).GetAccessControl();
        var fileOwner = security.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (fileOwner is null || !allowed.Contains(fileOwner)
            || security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Any(rule => rule.AccessControlType == AccessControlType.Allow
                    && !allowed.Contains(rule.IdentityReference.Value))) throw Rejected();
    }

    private static InvalidOperationException Rejected() => new("ZEK27_VERIFICATION_KEY_PATH_REJECTED");

    public void Dispose()
    {
        if (disposed || ownedDirectory is null) return;
        if ((File.GetAttributes(ownedDirectory) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0) throw Rejected();
        File.Delete(Path);
        Directory.Delete(ownedDirectory);
        disposed = true;
    }
}

public sealed class RegistrationVerificationKeyFixtureTests
{
    [Fact]
    public void Missing_input_creates_a_private_32_byte_file_and_scoped_disposal_removes_only_owned_paths()
    {
        string path;
        using (var key = RegistrationVerificationKeyFixture.Create(null))
        {
            path = key.Path;
            Assert.True(SystemPath.IsPathFullyQualified(path));
            Assert.Equal(32, new FileInfo(path).Length);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
        }
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(SystemPath.GetDirectoryName(path)));
    }

    [Fact]
    public void Independent_fixtures_use_distinct_runtime_material_without_printing_it()
    {
        using var first = RegistrationVerificationKeyFixture.Create(null);
        using var second = RegistrationVerificationKeyFixture.Create(null);
        var a = File.ReadAllBytes(first.Path);
        var b = File.ReadAllBytes(second.Path);
        try { Assert.False(CryptographicOperations.FixedTimeEquals(a, b)); }
        finally { CryptographicOperations.ZeroMemory(a); CryptographicOperations.ZeroMemory(b); }
    }

    [Fact]
    public void A_valid_explicit_file_is_borrowed_and_never_deleted_or_replaced()
    {
        using var owner = RegistrationVerificationKeyFixture.Create(null);
        using (var borrowed = RegistrationVerificationKeyFixture.Create(owner.Path)) Assert.Equal(owner.Path, borrowed.Path);
        Assert.True(File.Exists(owner.Path));
        Assert.Equal(32, new FileInfo(owner.Path).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-verification-key.bin")]
    public void Invalid_explicit_input_does_not_fall_back(string path)
        => Assert.Throws<InvalidOperationException>(() => RegistrationVerificationKeyFixture.Create(path));

    [Fact]
    public void A_missing_explicit_absolute_path_does_not_generate_replacement_material()
    {
        var path = SystemPath.Combine(SystemPath.GetTempPath(), "missing-test-verification-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => RegistrationVerificationKeyFixture.Create(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_nonprivate_explicit_file_is_rejected_even_when_its_size_and_path_are_valid()
    {
        using var owner = RegistrationVerificationKeyFixture.Create(null);
        var path = SystemPath.Combine(SystemPath.GetDirectoryName(owner.Path)!, "nonsecret-permission-canary.bin");
        // Public all-zero fixture data, never a usable issued credential or copied key.
        File.WriteAllBytes(path, new byte[32]);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var file = new FileInfo(path);
                var security = file.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read, AccessControlType.Allow));
                file.SetAccessControl(security);
            }
            else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.OtherRead);
            Assert.Throws<InvalidOperationException>(() => RegistrationVerificationKeyFixture.Create(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_wrong_size_explicit_file_is_rejected_instead_of_replaced()
    {
        using var owner = RegistrationVerificationKeyFixture.Create(null);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(owner.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(owner.Path, new byte[31]);
        Assert.Throws<InvalidOperationException>(() => RegistrationVerificationKeyFixture.Create(owner.Path));
        Assert.Equal(31, new FileInfo(owner.Path).Length);
    }
}
