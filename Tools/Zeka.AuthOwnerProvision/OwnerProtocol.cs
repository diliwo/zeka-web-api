using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zeka.AuthOwnerProvision;

[DebuggerDisplay("Owner provisioning input (secret values redacted)")]
internal sealed class OwnerInput
{
    public required string Schema { get; init; }
    public required string Scope { get; init; }
    public required string Project { get; init; }
    public required string AuthorityKind { get; init; }
    public required string OwnerDecisionId { get; init; }
    public required string ApprovalCommentId { get; init; }
    public required string OwnerAccount { get; init; }
    public required string ExpectedOwnerSid { get; init; }
    public required string ActualOwnerSid { get; init; }
    public required string ExpectedOwnerProfile { get; init; }
    public required string ActualOwnerProfile { get; init; }
    public required string NormalizedEmail { get; init; }
    public required string SourceCommit { get; init; }
    public required string BinarySha256 { get; init; }
    public required string DatabaseSourceCommit { get; init; }
    public required string DatabaseName { get; init; }
    public required string InitializationId { get; init; }
    public required Guid SubjectId { get; init; }
    public required string DisplayName { get; init; }
    public string? Slug { get; init; }
    public required string IdempotencyKey { get; init; }
    public required int ProcessingTimeoutSeconds { get; init; }
    public required int RetentionSeconds { get; init; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public required string AuthRuntimeConnectionString { get; init; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    public required string OwnerPassword { get; init; }
    public override string ToString() => "Owner provisioning input (secret values redacted).";
}

internal sealed record AssemblyBinding(string SourceCommit, string InformationalVersion, string BinarySha256)
{
    public static AssemblyBinding Read()
    {
        var assembly = typeof(AssemblyBinding).Assembly;
        var source = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(value => value.Key == "SourceCommit")?.Value ?? "";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "";
        using var stream = File.OpenRead(assembly.Location);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new(source, informational, hash);
    }
}

internal sealed class OwnerToolException(string code) : Exception("Owner provisioning rejected.")
{
    public string Code { get; } = code;
}

internal sealed class AdmittedOwnerInput
{
    internal AdmittedOwnerInput(OwnerInput value) => Value = value;
    internal OwnerInput Value { get; }
    public override string ToString() => "Admitted local synthetic owner (secret values redacted).";
}

internal static class OwnerProtocol
{
    public const string InputSchema = "zeka-m1-owner-provision/v1";
    public const string ApprovedOwner = @"DILI_LP\herve";
    public const string ApprovedProfile = @"C:\Users\herve";
    public const string ApprovedEmail = "HERVE.MBILO@GMAIL.COM";
    public const string OwnerDecision = "a7daf3ea-6370-4604-9e4b-e5eb206465c7";
    public const int MaximumInputBytes = 65536;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static OwnerInput Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaximumInputBytes)
                throw new OwnerToolException("INPUT_SIZE_REJECTED");
            using var document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false });
            RejectDuplicateMembers(document.RootElement);
            return JsonSerializer.Deserialize<OwnerInput>(bytes.Span, JsonOptions)
                ?? throw new OwnerToolException("INPUT_SCHEMA_REJECTED");
        }
        catch (OwnerToolException) { throw; }
        catch { throw new OwnerToolException("INPUT_SCHEMA_REJECTED"); }
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new OwnerToolException("INPUT_SCHEMA_REJECTED");
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateMembers(item);
    }

    internal static AdmittedOwnerInput Admit(OwnerInput input, AssemblyBinding binding)
    {
        if (input.Schema != InputSchema || input.Scope != "LocalSyntheticM1OwnerProvision"
            || input.Project != "zeka-m1" || input.AuthorityKind != "OwnerAttestedLocalSynthetic"
            || input.OwnerDecisionId != OwnerDecision || input.ApprovalCommentId != "6529021741")
            throw new OwnerToolException("OWNER_SCOPE_REJECTED");
        if (!string.Equals(input.OwnerAccount, ApprovedOwner, StringComparison.OrdinalIgnoreCase)
            || !Regex.IsMatch(input.ExpectedOwnerSid ?? "", @"^S-1-5-21-\d+-\d+-\d+-\d+$")
            || input.ActualOwnerSid != input.ExpectedOwnerSid
            || !string.Equals(input.ExpectedOwnerProfile, ApprovedProfile, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(input.ActualOwnerProfile, ApprovedProfile, StringComparison.OrdinalIgnoreCase)
            || input.NormalizedEmail != ApprovedEmail)
            throw new OwnerToolException("OWNER_IDENTITY_REJECTED");
        if (!IsSha(input.SourceCommit, 40) || !IsSha(input.BinarySha256, 64)
            || binding.SourceCommit != input.SourceCommit
            || binding.InformationalVersion != "0.1.0+" + input.SourceCommit
            || binding.BinarySha256 != input.BinarySha256)
            throw new OwnerToolException("SOURCE_BINDING_REJECTED");
        if (!IsSha(input.DatabaseSourceCommit, 40)
            || !Regex.IsMatch(input.InitializationId ?? "", "^[A-Za-z0-9_-]{16,128}$")
            || input.DatabaseName != "zeka_m1_auth")
            throw new OwnerToolException("DATABASE_BINDING_REJECTED");
        if (input.SubjectId == Guid.Empty || string.IsNullOrWhiteSpace(input.IdempotencyKey)
            || input.IdempotencyKey.Length > 200 || string.IsNullOrWhiteSpace(input.DisplayName)
            || input.DisplayName.Trim().Length > 200 || input.OwnerPassword is null
            || input.OwnerPassword.Length is < 1 or > 512
            || input.ProcessingTimeoutSeconds != 120 || input.RetentionSeconds != 86400)
            throw new OwnerToolException("PROVISIONING_INPUT_REJECTED");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(input.AuthRuntimeConnectionString);
            if (connection.Host != "postgres" || connection.Port != 5432
                || connection.Database != input.DatabaseName || connection.Username != "zeka_auth_runtime"
                || string.IsNullOrEmpty(connection.Password) || !string.IsNullOrEmpty(connection.Options)
                || !string.IsNullOrEmpty(connection.SearchPath))
                throw new OwnerToolException("RUNTIME_CONNECTION_REJECTED");
        }
        catch (OwnerToolException) { throw; }
        catch { throw new OwnerToolException("RUNTIME_CONNECTION_REJECTED"); }
        return new(input);
    }

    private static bool IsSha(string? value, int length) =>
        value is not null && Regex.IsMatch(value, "^[0-9a-f]{" + length + "}$");
}

internal sealed record OwnerReceipt(
    string Schema, string Status, string Code, string Stage, int ExitCode,
    string? SourceCommit, string? BinarySha256, Guid? SubjectId = null,
    Guid? OrganisationId = null, Guid? MembershipId = null,
    string AuthorityKind = "OwnerAttestedLocalSynthetic",
    string AuditMarker = "operator-provisioned, no agreements")
{
    internal static string Serialize(OwnerReceipt receipt) =>
        JsonSerializer.Serialize(receipt, OwnerProtocol.JsonOptions);
}
