using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Zeka.WorkerCredentials;

// Native Linux protection is deliberately mandatory in the production reader.
// Ordinary Windows tests inject a reader with explicit synthetic metadata; there is no runtime fallback.
public sealed class LinuxProtectedWorkerCredentialReader : IProtectedWorkerCredentialReader
{
    private const int DirectoryFlag = 0x10000, NoFollow = 0x20000, CloseOnExec = 0x80000;
    private const int EmptyPath = 0x1000, NonBlocking = 0x800, PathOnly = 0x200000;
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, [Out] byte[] stat);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUser();
    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint EffectiveGroup();

    public async ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || EffectiveUser() != 1654 || EffectiveGroup() != 1654)
            throw new WorkerCredentialUnavailableException("WORKER_NATIVE_IDENTITY_REJECTED");
        if (string.IsNullOrEmpty(directory) || !directory.StartsWith("/", StringComparison.Ordinal)
            || directory.Contains('\\') || directory.Split('/').Any(x => x is "." or ".."))
            throw new WorkerCredentialUnavailableException("WORKER_DIRECTORY_REJECTED");
        SafeFileHandle? directoryHandle = null;
        byte[]? output = null;
        try
        {
            directoryHandle = Handle(Open("/", DirectoryFlag | NoFollow | CloseOnExec));
            foreach (var component in directory.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var next = Handle(OpenAt(Fd(directoryHandle), component, DirectoryFlag | NoFollow | CloseOnExec));
                directoryHandle.Dispose();
                directoryHandle = next;
            }
            var directoryBefore = Metadata(directoryHandle);
            Validate(directoryBefore, isDirectory: true);
            using var pathHandle = Handle(OpenAt(Fd(directoryHandle), WorkerCredentialEncoding.FileName, PathOnly | NoFollow | CloseOnExec));
            var admitted = Metadata(pathHandle);
            Validate(admitted, isDirectory: false);
            // Reopen only the already-admitted regular inode. No FIFO or device is opened for I/O.
            using var fileHandle = Handle(Open("/proc/self/fd/" + Fd(pathHandle), CloseOnExec | NonBlocking));
            var before = Metadata(fileHandle);
            Validate(before, isDirectory: false);
            if (before != admitted) throw new WorkerCredentialUnavailableException("WORKER_FILE_CHANGED");
            if (before.Size == 0 || before.Size > WorkerCredentialEncoding.MaximumBytes)
                throw new WorkerCredentialUnavailableException("WORKER_FILE_SIZE_REJECTED");
            output = new byte[checked((int)before.Size)];
            using var file = new FileStream(fileHandle, FileAccess.Read, 4096, isAsync: false);
            var position = 0;
            while (position < output.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await file.ReadAsync(output.AsMemory(position), cancellationToken);
                if (read == 0) throw new WorkerCredentialUnavailableException("WORKER_FILE_INCOMPLETE");
                position += read;
            }
            if (file.ReadByte() != -1) throw new WorkerCredentialUnavailableException("WORKER_FILE_CHANGED");
            var after = Metadata(fileHandle);
            var directoryAfter = Metadata(directoryHandle);
            Validate(after, isDirectory: false);
            Validate(directoryAfter, isDirectory: true);
            if (before != after || directoryBefore != directoryAfter)
                throw new WorkerCredentialUnavailableException("WORKER_FILE_CHANGED");
            var result = output;
            output = null;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerCredentialUnavailableException) { throw; }
        catch { throw new WorkerCredentialUnavailableException("WORKER_PROTECTED_FILE_UNAVAILABLE"); }
        finally
        {
            directoryHandle?.Dispose();
            if (output is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(output);
        }
    }

    private static SafeFileHandle Handle(int value)
    {
        if (value < 0) throw new WorkerCredentialUnavailableException("WORKER_PROTECTED_FILE_UNAVAILABLE");
        return new SafeFileHandle((IntPtr)value, ownsHandle: true);
    }
    private static int Fd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle());
    private readonly record struct FileMetadata(uint User, uint Group, ushort Mode, uint Links, ulong Inode, ulong Size,
        long ModifiedSeconds, uint ModifiedNanos, long ChangedSeconds, uint ChangedNanos);
    private static FileMetadata Metadata(SafeFileHandle handle)
    {
        // Linux statx UAPI layout is fixed across supported architectures; allocate its full256-byte buffer.
        var buffer = new byte[256];
        if (Statx(Fd(handle), "", EmptyPath, 0x7ff, buffer) != 0
            || (BitConverter.ToUInt32(buffer, 0) & 0x3df) != 0x3df)
            throw new WorkerCredentialUnavailableException("WORKER_FILE_METADATA_UNAVAILABLE");
        return new(BitConverter.ToUInt32(buffer, 20), BitConverter.ToUInt32(buffer, 24),
            BitConverter.ToUInt16(buffer, 28), BitConverter.ToUInt32(buffer, 16),
            BitConverter.ToUInt64(buffer, 32), BitConverter.ToUInt64(buffer, 40),
            BitConverter.ToInt64(buffer, 112), BitConverter.ToUInt32(buffer, 120),
            BitConverter.ToInt64(buffer, 96), BitConverter.ToUInt32(buffer, 104));
    }
    private static void Validate(FileMetadata value, bool isDirectory)
    {
        var type = value.Mode & 0xf000;
        var permissions = value.Mode & 0x0fff;
        if (value.User != 1654 || value.Group != 1654
            || type != (isDirectory ? 0x4000 : 0x8000)
            || permissions != (isDirectory ? 0x140 : 0x100)
            || (!isDirectory && value.Links != 1))
            throw new WorkerCredentialUnavailableException("WORKER_FILE_PROTECTION_REJECTED");
    }
}