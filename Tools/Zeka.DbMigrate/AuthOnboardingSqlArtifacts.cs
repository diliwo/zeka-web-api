using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zeka.DbMigrate;

internal sealed record AuthOnboardingSqlArtifacts(string MigrationSql, string GrantSql)
{
    internal const string FromMigration = "20261018144506_BrowserSessionSnapshot";
    internal const string TargetMigration = "20261115195218_OnboardingRegistration";
    internal const string MigrationSha256 = "b68a058177c27b67e22c81e11cf6ec3c0eedbe69833937b534057bc2af7075e2";
    internal const string GrantSha256 = "b897bc6e7f933a538142696b81644d91c4d9439cb5ee7db1933106c52b9ac841";
    internal const string ManifestSha256 = "6eca66966a8957a7dfc558d308992c2a92e9dd324ae3bcf2b232f583f314b944";

    private const string MigrationResource = "Zeka.DbMigrate.AuthOnboarding.onboarding-migration.sql";
    private const string GrantResource = "Zeka.DbMigrate.AuthOnboarding.onboarding-grants.sql";
    private const int MaximumResourceBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions SqlPatternOptions = RegexOptions.CultureInvariant
        | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking;

    // Sanity checks supplement the approved raw-byte hashes; this is not a SQL parser.
    private static readonly Regex TransactionControl = new(
        @"(?:\A|[;\r\n])\s*(?:BEGIN\b|START\s+TRANSACTION\b|COMMIT\b|END\b|ROLLBACK\b|ABORT\b|SAVEPOINT\b|RELEASE\b|PREPARE\s+TRANSACTION\b|SET\s+TRANSACTION\b|SET\s+SESSION\s+CHARACTERISTICS\s+AS\s+TRANSACTION\b)",
        SqlPatternOptions, PatternTimeout);
    private static readonly Regex TargetHistoryInsert = new(
        @"\bINSERT\s+INTO\s+(?:(?:""public""|public)\s*\.\s*)?""__EFMigrationsHistory""\s*\(\s*""MigrationId""\s*,\s*""ProductVersion""\s*\)\s*VALUES\s*\(\s*'"
        + Regex.Escape(TargetMigration) + @"'\s*,\s*'[^'\r\n]+'\s*\)\s*;",
        SqlPatternOptions, PatternTimeout);
    private static readonly Regex ManifestResourceName = new(
        @"(?:^|\.)rls-manifest\.v[0-9]+\.json\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, PatternTimeout);

    internal static AuthOnboardingSqlArtifacts Load()
    {
        try
        {
            var assembly = typeof(AuthOnboardingSqlArtifacts).Assembly;
            var migration = DecodeStrictUtf8(ReadBoundResource(assembly, MigrationResource, MigrationSha256));
            var grants = DecodeStrictUtf8(ReadBoundResource(assembly, GrantResource, GrantSha256));
            if (TransactionControl.IsMatch(migration) || TransactionControl.IsMatch(grants)
                || TargetHistoryInsert.Count(migration) != 1)
                throw new InvalidDataException();
            return new(migration, grants);
        }
        catch
        {
            // Never expose resource contents, SQL, paths or decoder/exception details.
            throw new InvalidOperationException("AUTH_ONBOARDING_SQL_ARTIFACT_REJECTED");
        }
    }

    internal static void VerifyManifestBinding(Assembly authAssembly)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(authAssembly);
            var resources = authAssembly.GetManifestResourceNames()
                .Where(name => ManifestResourceName.IsMatch(name)).ToArray();
            if (resources.Length != 1 || !resources[0].EndsWith("rls-manifest.v20.json", StringComparison.Ordinal))
                throw new InvalidDataException();
            var bytes = ReadBoundResource(authAssembly, resources[0], ManifestSha256);
            using var manifest = JsonDocument.Parse(DecodeStrictUtf8(bytes));
            if (manifest.RootElement.GetProperty("schemaVersion").GetInt32() != 20
                || manifest.RootElement.GetProperty("service").GetString() != "AuthManagement")
                throw new InvalidDataException();
        }
        catch
        {
            throw new InvalidOperationException("AUTH_ONBOARDING_MANIFEST_BINDING_REJECTED");
        }
    }

    private static byte[] ReadBoundResource(Assembly assembly, string name, string expectedSha256)
    {
        using var input = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > MaximumResourceBytes) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (bytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException();
        return bytes;
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        // A BOM is transport metadata. Preserve every SQL character and CRLF unchanged.
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
    }
}