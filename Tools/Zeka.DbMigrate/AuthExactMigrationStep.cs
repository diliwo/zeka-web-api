using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Zeka.DbMigrate;

// Two explicitly approved steps, not a generic migration/latest execution authority.
internal sealed class AuthExactMigrationStep
{
    internal const string OwnerTarget = OrganisationOwnerIndexContract.MigrationId;
    internal const string OwnerMigrationSha256 = "8b2b5ad1ac51c7d0038e127b75791324c11bf4a76c699524b31feee084249400";
    internal const string OwnerManifestSha256 = "93157072e70a23718e7f943f9e4340970e638da8347b06b4d672a47555e2baa0";
    internal const string NoGrantSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string HistoricalManifest = "Zeka.DbMigrate.AuthOnboarding.historical-v20.json";
    private const string OwnerSql = "Zeka.DbMigrate.AuthOnboarding.organisation-owner-index-migration.sql";
    private readonly string manifestJson;

    private AuthExactMigrationStep(bool ownerIndex, AuthOnboardingSqlArtifacts artifacts, string manifest)
    {
        IsOwnerIndex = ownerIndex;
        Artifacts = artifacts;
        manifestJson = manifest;
    }

    internal bool IsOwnerIndex { get; }
    internal AuthOnboardingSqlArtifacts Artifacts { get; }
    internal string From => IsOwnerIndex ? AuthOnboardingSqlArtifacts.TargetMigration : AuthOnboardingSqlArtifacts.FromMigration;
    internal string Target => IsOwnerIndex ? OwnerTarget : AuthOnboardingSqlArtifacts.TargetMigration;
    internal string MigrationSha256 => IsOwnerIndex ? OwnerMigrationSha256 : AuthOnboardingSqlArtifacts.MigrationSha256;
    internal string GrantSha256 => IsOwnerIndex ? NoGrantSha256 : AuthOnboardingSqlArtifacts.GrantSha256;
    internal string ManifestSha256 => IsOwnerIndex ? OwnerManifestSha256 : AuthOnboardingSqlArtifacts.ManifestSha256;
    internal string FullManifestStage => IsOwnerIndex ? "FULL_V21_VERIFICATION" : "FULL_V20_VERIFICATION";

    internal static bool IsTarget(string target) => target is AuthOnboardingSqlArtifacts.TargetMigration or OwnerTarget;

    internal static AuthExactMigrationStep Load(string target)
    {
        if (!IsTarget(target)) throw new InvalidOperationException("ATOMIC_AUTH_EXACT_TARGET_REQUIRED");
        var active = ReadActiveManifest(typeof(AuthDbContext).Assembly);
        if (target == OwnerTarget)
        {
            var sql = ReadBound(typeof(AuthExactMigrationStep).Assembly, OwnerSql, OwnerMigrationSha256);
            // The generated SQL includes exactly one target history insert; transaction control stays with the executor.
            var timeout = TimeSpan.FromMilliseconds(100);
            var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
            if (Regex.IsMatch(sql, @"(?:\A|[;\r\n])\s*(?:BEGIN\s*;|START\s+TRANSACTION\b|COMMIT\b|ROLLBACK\b|SAVEPOINT\b)", options, timeout)
                || Regex.Matches(sql, "VALUES \\('" + Regex.Escape(OwnerTarget) + "', '8\\.0\\.8'\\);", options, timeout).Count != 1)
                throw new InvalidOperationException("AUTH_OWNER_INDEX_SQL_ARTIFACT_REJECTED");
            return new(true, new AuthOnboardingSqlArtifacts(sql, string.Empty), active);
        }
        var previous = ReadBound(typeof(AuthExactMigrationStep).Assembly, HistoricalManifest,
            AuthOnboardingSqlArtifacts.ManifestSha256);
        VerifyManifestIdentity(previous, 20, AuthOnboardingSqlArtifacts.TargetMigration);
        return new(false, AuthOnboardingSqlArtifacts.Load(), previous);
    }

    internal static void VerifyActiveManifestBinding(Assembly assembly) => _ = ReadActiveManifest(assembly);

    private static string ReadActiveManifest(Assembly assembly)
    {
        var resources = assembly.GetManifestResourceNames()
            .Where(name => Regex.IsMatch(name, @"(?:^|\.)rls-manifest\.v[0-9]+\.json\z",
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))).ToArray();
        if (resources.Length != 1 || !resources[0].EndsWith("rls-manifest.v21.json", StringComparison.Ordinal))
            throw new InvalidOperationException("AUTH_ONBOARDING_MANIFEST_BINDING_REJECTED");
        var manifest = ReadBound(assembly, resources[0], OwnerManifestSha256);
        VerifyManifestIdentity(manifest, 21, OwnerTarget);
        return manifest;
    }

    private static void VerifyManifestIdentity(string json, int version, string target)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != version
            || document.RootElement.GetProperty("service").GetString() != "AuthManagement"
            || document.RootElement.GetProperty("migrationId").GetString() != target)
            throw new InvalidOperationException("AUTH_ONBOARDING_MANIFEST_BINDING_REJECTED");
    }

    private static string ReadBound(Assembly assembly, string name, string expectedHash)
    {
        using var input = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("AUTH_ONBOARDING_SQL_ARTIFACT_REJECTED");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + count > 1024 * 1024) throw new InvalidOperationException("AUTH_ONBOARDING_SQL_ARTIFACT_REJECTED");
            output.Write(buffer, 0, count);
        }
        var bytes = output.ToArray();
        if (bytes.Length == 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expectedHash)))
            throw new InvalidOperationException("AUTH_ONBOARDING_SQL_ARTIFACT_REJECTED");
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    internal string[] ExpectedCompiledHistory(DbContext context)
    {
        var compiled = context.Database.GetMigrations().ToArray();
        if (compiled.Length < 3 || compiled[^3] != AuthOnboardingSqlArtifacts.FromMigration
            || compiled[^2] != AuthOnboardingSqlArtifacts.TargetMigration || compiled[^1] != OwnerTarget)
            throw new InvalidOperationException("ATOMIC_AUTH_COMPILED_HISTORY_REJECTED");
        return IsOwnerIndex ? compiled : compiled[..^1];
    }

    // Only hash-bound manifests from the private constructor can reach this overload.
    internal async Task VerifyManifestAsync(DbContext database, CancellationToken cancellationToken)
    {
        var assembly = typeof(AuthDbContext).Assembly;
        var type = assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifest", true)!;
        var value = JsonSerializer.Deserialize(manifestJson, type, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("AUTH_ONBOARDING_MANIFEST_BINDING_REJECTED");
        var verifier = assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifestVerifier", true)!;
        var method = verifier.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(candidate => candidate.Name == "VerifyAsync"
            && candidate.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }, _]
            && first == typeof(DbContext) && second == type);
        await ((Task)method.Invoke(null, [database, value, cancellationToken])!).ConfigureAwait(false);
    }
}
