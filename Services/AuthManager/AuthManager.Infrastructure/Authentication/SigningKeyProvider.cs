using System.Security.Cryptography;
namespace AuthManager.Infrastructure.Authentication;

// The host supplies references. No generation, provisioning, or downstream private-key access.
public interface ISigningKeyProvider
{
    Task<RSA> OpenPrivateKeyAsync(string reference, CancellationToken cancellationToken);
    Task<RSA> OpenPublicKeyAsync(string reference, CancellationToken cancellationToken);
}
public sealed class FileSigningKeyProvider : ISigningKeyProvider
{
    public Task<RSA> OpenPrivateKeyAsync(string reference, CancellationToken cancellationToken) => Open(reference, true, cancellationToken);
    public Task<RSA> OpenPublicKeyAsync(string reference, CancellationToken cancellationToken) => Open(reference, false, cancellationToken);
    private static async Task<RSA> Open(string reference, bool signing, CancellationToken cancellationToken)
    {
        RSA? rsa = null;
        try
        {
            var info = new FileInfo(reference);
            if (!info.Exists || info.Length > 16384) throw new InvalidOperationException();
            var pem = await File.ReadAllTextAsync(reference, cancellationToken);
            rsa = RSA.Create(); rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048) throw new InvalidOperationException();
            if (signing) rsa.ExportParameters(true);
            else
            {
                // Retiring references contain public PEM only.
                if (pem.Contains("PRIVATE", StringComparison.Ordinal)) throw new InvalidOperationException();
            }
            return rsa;
        }
        catch
        {
            rsa?.Dispose();
            throw new Zeka.Authentication.AuthenticationAuthorityUnavailableException();
        }
    }
}
