using System.Security;
using System.Security.Cryptography;
using System.Text;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Authorization;

namespace ClientManagement.API.LocalDevelopment;

internal sealed class LocalActionAuthorityCredential(
    IActionWriteActivationGate gate, string? authority, string? file) : IActionAuthorityServiceCredential
{
    private const int MaxBytes = 8192;

    public string BearerToken
    {
        get
        {
            gate.DemandActionWrite();
            if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file)
                || !Uri.TryCreate(authority, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw Unavailable();

            var buffer = new byte[MaxBytes + 1];
            try
            {
                var info = new FileInfo(file);
                if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                    || info.Length is 0 or > MaxBytes)
                    throw Unavailable();

                using var stream = info.Open(FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var length = stream.ReadAtLeast(buffer, buffer.Length, false);
                if (length is 0 or > MaxBytes) throw Unavailable();

                var text = Encoding.ASCII.GetString(buffer, 0, length);
                if (text.EndsWith('\n')) text = text[..^1];
                if (text.Length == length - 1 && text.EndsWith('\r')) text = text[..^1];
                var segments = text.Split('.');
                if (segments.Length != 3 || segments.Any(segment => segment.Length == 0
                    || segment.Any(value => !char.IsAsciiLetterOrDigit(value) && value is not ('-' or '_'))))
                    throw Unavailable();
                return text;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or SecurityException)
            {
                throw Unavailable();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static TenantAccessException Unavailable() => new(AccessFailure.Unavailable);
}
