using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Zeka.DbMigrate;

internal sealed class EvidenceOutput
{
    private readonly string root;
    private readonly string[] parentComponents;
    private readonly DirectoryIdentity[] directoryIdentities;
    private readonly string finalName;
    private readonly SafeFileHandle parentHandle;

    private EvidenceOutput(string root, string[] parentComponents, DirectoryIdentity[] directoryIdentities,
        string finalName, SafeFileHandle parentHandle)
    {
        this.root = root;
        this.parentComponents = parentComponents;
        this.directoryIdentities = directoryIdentities;
        this.finalName = finalName;
        this.parentHandle = parentHandle;
    }

    public static EvidenceOutput Create(string authorizedRoot, string relativeFile)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(authorizedRoot)
            || string.IsNullOrWhiteSpace(relativeFile) || Path.IsPathRooted(relativeFile)
            || relativeFile.IndexOf(':') >= 0 || relativeFile.StartsWith('\\') || relativeFile.Contains('\0'))
            throw new EvidencePathException();

        var root = Path.GetFullPath(authorizedRoot);
        var components = relativeFile.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.None);
        if (components.Length == 0 || components.Any(x => x is "" or "." or ".."))
            throw new EvidencePathException();

        var parentComponents = components[..^1];
        SafeFileHandle? current = null;
        try
        {
            current = OpenRoot(root);
            var identities = new List<DirectoryIdentity> { GetDirectoryIdentity(current) };
            foreach (var component in parentComponents)
            {
                var next = OpenDirectory(current, component);
                current.Dispose();
                current = next;
                identities.Add(GetDirectoryIdentity(current));
            }

            RequireMissing(current, components[^1]);
            var output = new EvidenceOutput(root, parentComponents, identities.ToArray(), components[^1], current);
            current = null;
            return output;
        }
        catch (EvidencePathException)
        {
            throw;
        }
        catch
        {
            throw new EvidencePathException();
        }
        finally
        {
            current?.Dispose();
        }
    }

    public async Task PublishAsync(MigrationEvidence evidence, CancellationToken cancellationToken = default)
    {
        var temporaryName = $".{finalName}.{Guid.NewGuid():N}.tmp";
        var published = false;
        try
        {
            RevalidateDirectoryChain();
            RequireMissing(parentHandle, finalName);

            using (var temporaryHandle = CreateExclusiveFile(parentHandle, temporaryName))
            await using (var stream = new FileStream(temporaryHandle, FileAccess.Write, 4096, false))
            {
                await JsonSerializer.SerializeAsync(stream, evidence,
                    new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                if (NativeMethods.fsync(Descriptor(temporaryHandle)) != 0) ThrowNativeFailure();
            }

            RevalidateDirectoryChain();
            if (NativeMethods.renameat2(Descriptor(parentHandle), temporaryName, Descriptor(parentHandle), finalName,
                    NativeMethods.RenameNoReplace) != 0)
                ThrowNativeFailure();
            published = true;
        }
        catch (EvidencePathException)
        {
            throw;
        }
        catch
        {
            throw new EvidencePathException();
        }
        finally
        {
            if (!published) _ = NativeMethods.unlinkat(Descriptor(parentHandle), temporaryName, 0);
            parentHandle.Dispose();
        }
    }

    private void RevalidateDirectoryChain()
    {
        SafeFileHandle? current = null;
        try
        {
            current = OpenRoot(root);
            RequireIdentity(current, directoryIdentities[0]);
            for (var index = 0; index < parentComponents.Length; index++)
            {
                var next = OpenDirectory(current, parentComponents[index]);
                current.Dispose();
                current = next;
                RequireIdentity(current, directoryIdentities[index + 1]);
            }
        }
        finally
        {
            current?.Dispose();
        }
    }

    private static SafeFileHandle OpenRoot(string root)
    {
        var current = OpenDirectory(null, Path.DirectorySeparatorChar.ToString());
        try
        {
            foreach (var component in root.Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var next = OpenDirectory(current, component);
                current.Dispose();
                current = next;
            }

            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(SafeFileHandle? parent, string component)
    {
        var descriptor = NativeMethods.openat(parent is null ? NativeMethods.CurrentWorkingDirectory : Descriptor(parent),
            component, NativeMethods.DirectoryOpenFlags, 0);
        if (descriptor < 0) ThrowNativeFailure();
        return new SafeFileHandle((IntPtr)descriptor, true);
    }

    private static SafeFileHandle CreateExclusiveFile(SafeFileHandle parent, string name)
    {
        var descriptor = NativeMethods.openat(Descriptor(parent), name, NativeMethods.ExclusiveFileCreateFlags,
            NativeMethods.OwnerReadWriteMode);
        if (descriptor < 0) ThrowNativeFailure();
        return new SafeFileHandle((IntPtr)descriptor, true);
    }

    private static void RequireMissing(SafeFileHandle parent, string name)
    {
        if (NativeMethods.statx(Descriptor(parent), name, NativeMethods.NoFollowSymlink, NativeMethods.StatxType,
                out _) == 0)
            throw new EvidencePathException();
        if (Marshal.GetLastPInvokeError() != NativeMethods.NoSuchFileOrDirectory) ThrowNativeFailure();
    }

    private static DirectoryIdentity GetDirectoryIdentity(SafeFileHandle handle)
    {
        if (NativeMethods.statx(Descriptor(handle), string.Empty,
                NativeMethods.EmptyPath | NativeMethods.NoFollowSymlink,
                NativeMethods.StatxType | NativeMethods.StatxInode | NativeMethods.StatxMountId, out var status) != 0)
            ThrowNativeFailure();
        if ((status.Mask & (NativeMethods.StatxType | NativeMethods.StatxInode | NativeMethods.StatxMountId))
            != (NativeMethods.StatxType | NativeMethods.StatxInode | NativeMethods.StatxMountId))
            throw new EvidencePathException();
        if ((status.Mode & NativeMethods.FileTypeMask) != NativeMethods.DirectoryType)
            throw new EvidencePathException();
        return new DirectoryIdentity(status.DeviceMajor, status.DeviceMinor, status.Inode, status.MountId);
    }

    private static void RequireIdentity(SafeFileHandle handle, DirectoryIdentity expected)
    {
        if (GetDirectoryIdentity(handle) != expected) throw new EvidencePathException();
    }

    private static int Descriptor(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());

    private static void ThrowNativeFailure()
        => throw new IOException("Evidence path operation failed.", new Win32Exception(Marshal.GetLastPInvokeError()));

    private readonly record struct DirectoryIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong MountId);

    private static class NativeMethods
    {
        internal const int CurrentWorkingDirectory = -100;
        internal const int DirectoryOpenFlags = 0x10000 | 0x20000 | 0x80000;
        internal const int ExclusiveFileCreateFlags = 0x1 | 0x40 | 0x80 | 0x20000 | 0x80000;
        internal const uint OwnerReadWriteMode = 0x180;
        internal const int NoSuchFileOrDirectory = 2;
        internal const int NoFollowSymlink = 0x100;
        internal const int EmptyPath = 0x1000;
        internal const uint StatxType = 0x1;
        internal const uint StatxInode = 0x100;
        internal const uint StatxMountId = 0x1000;
        internal const ushort FileTypeMask = 0xf000;
        internal const ushort DirectoryType = 0x4000;
        internal const uint RenameNoReplace = 1;

        [DllImport("libc", SetLastError = true)]
        internal static extern int openat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
            uint mode);

        [DllImport("libc", SetLastError = true)]
        internal static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
            uint mask, out Statx status);

        [DllImport("libc", SetLastError = true)]
        internal static extern int renameat2(int oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            int newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int unlinkat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", SetLastError = true)]
        internal static extern int fsync(int descriptor);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Statx
    {
        internal uint Mask;
        internal uint BlockSize;
        internal ulong Attributes;
        internal uint LinkCount;
        internal uint UserId;
        internal uint GroupId;
        internal ushort Mode;
        internal ushort Spare0;
        internal ulong Inode;
        internal ulong Size;
        internal ulong Blocks;
        internal ulong AttributesMask;
        internal StatxTimestamp AccessTime;
        internal StatxTimestamp BirthTime;
        internal StatxTimestamp ChangeTime;
        internal StatxTimestamp ModificationTime;
        internal uint SpecialDeviceMajor;
        internal uint SpecialDeviceMinor;
        internal uint DeviceMajor;
        internal uint DeviceMinor;
        internal ulong MountId;
        internal uint DirectIoMemoryAlignment;
        internal uint DirectIoOffsetAlignment;
        internal ulong Spare1;
        internal ulong Spare2;
        internal ulong Spare3;
        internal ulong Spare4;
        internal ulong Spare5;
        internal ulong Spare6;
        internal ulong Spare7;
        internal ulong Spare8;
        internal ulong Spare9;
        internal ulong Spare10;
        internal ulong Spare11;
        internal ulong Spare12;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct StatxTimestamp
    {
        internal readonly long Seconds;
        internal readonly uint Nanoseconds;
        internal readonly int Reserved;
    }
}

internal sealed class EvidencePathException : Exception;
