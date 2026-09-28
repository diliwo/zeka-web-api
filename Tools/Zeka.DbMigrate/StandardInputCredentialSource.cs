namespace Zeka.DbMigrate;

internal static class StandardInputCredentialSource
{
    private const int MaximumCredentialLength = 8192;

    public static async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new char[MaximumCredentialLength + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await Console.In.ReadAsync(buffer.AsMemory(count, 1), cancellationToken);
            if (read == 0 || buffer[count] is '\r' or '\n') break;
            count += read;
        }
        if (count == 0 || count > MaximumCredentialLength) throw new CredentialSourceException();
        return new string(buffer, 0, count);
    }
}

internal sealed class CredentialSourceException : Exception;
