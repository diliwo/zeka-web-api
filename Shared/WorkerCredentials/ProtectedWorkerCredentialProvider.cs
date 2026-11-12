using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Zeka.WorkerCredentials;

public interface IWorkerCredentialProvider
{
    ValueTask<WorkerCredentialSnapshot> ReadAsync(CancellationToken cancellationToken);
    void MarkSuccessfulUse(WorkerCredentialSnapshot snapshot);
}

public interface IProtectedWorkerCredentialReader
{
    ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken);
}

[DebuggerDisplay("Worker credential snapshot (redacted)")]
public sealed class WorkerCredentialSnapshot
{
    internal WorkerCredentialSnapshot(WorkerCredentialPackage value)
    {
        OperationId = value.OperationId; Generation = value.Generation; SubjectId = value.SubjectId;
        SessionId = value.SessionId; Profile = value.Profile; OrganisationIds = Array.AsReadOnly(value.OrganisationIds.ToArray());
        TokenIssuedAtUtc = value.TokenIssuedAtUtc; AccessExpiresAtUtc = value.AccessExpiresAtUtc;
        SessionCreatedAtUtc = value.SessionCreatedAtUtc; SessionExpiresAtUtc = value.SessionExpiresAtUtc; Token = value.Token; PayloadSha256 = value.PayloadSha256;
    }
    public Guid OperationId { get; }
    public long Generation { get; }
    public Guid SubjectId { get; }
    public Guid SessionId { get; }
    public string Profile { get; }
    public IReadOnlyList<Guid> OrganisationIds { get; }
    public DateTimeOffset TokenIssuedAtUtc { get; }
    public DateTimeOffset AccessExpiresAtUtc { get; }
    public DateTimeOffset SessionCreatedAtUtc { get; }
    public DateTimeOffset SessionExpiresAtUtc { get; }
    [DebuggerBrowsable(DebuggerBrowsableState.Never)] public string Token { get; }
    public string PayloadSha256 { get; }
    public override string ToString() => "Worker credential snapshot (redacted)";
}

public sealed class WorkerCredentialUnavailableException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

public sealed class WorkerCredentialOptions
{
    public const string MountedDirectory = "/run/zeka-worker-credential";
    public required string Profile { get; init; }
    public required Guid SubjectId { get; init; }
    public required Guid[] OrganisationIds { get; init; }
    public required string Directory { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }

    public static WorkerCredentialOptions Read(IConfiguration configuration, string profile)
    {
        if (!string.Equals(configuration["TenantWorker:CredentialRotationEnabled"], "true", StringComparison.OrdinalIgnoreCase))
            throw new WorkerCredentialUnavailableException("WORKER_ROTATION_NOT_ENABLED");
        if (!string.IsNullOrEmpty(configuration["TenantWorker:BearerToken"]))
            throw new WorkerCredentialUnavailableException("STATIC_WORKER_TOKEN_REJECTED");
        var directory = configuration["TenantWorker:CredentialDirectory"];
        var organisations = configuration.GetSection("TenantWorker:OrganisationIds").Get<string[]>() ?? [];
        if (directory != MountedDirectory
            || !Guid.TryParse(configuration["TenantWorker:SubjectId"], out var subject) || subject == Guid.Empty
            || organisations.Length == 0 || organisations.Length > 100
            || organisations.Any(x => !Guid.TryParse(x, out var id) || id == Guid.Empty)
            || string.IsNullOrWhiteSpace(configuration["Authentication:Issuer"])
            || string.IsNullOrWhiteSpace(configuration["Authentication:Audience"]))
            throw new WorkerCredentialUnavailableException("WORKER_PROVIDER_CONFIGURATION_REJECTED");
        return new()
        {
            Profile = profile, SubjectId = subject,
            OrganisationIds = organisations.Select(Guid.Parse).Distinct().OrderBy(x => x).ToArray(),
            Directory = directory, Issuer = configuration["Authentication:Issuer"]!,
            Audience = configuration["Authentication:Audience"]!
        };
    }
}

public sealed class ProtectedWorkerCredentialProvider : IWorkerCredentialProvider
{
    private static readonly Guid ProcessInstance = Guid.NewGuid();
    private readonly IProtectedWorkerCredentialReader files;
    private readonly WorkerCredentialOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<ProtectedWorkerCredentialProvider> logger;
    private readonly object generationLock = new();
    private long highestGeneration;
    private string? highestPayloadHash;
    private readonly object successfulLock = new();
    private long lastSuccessfulGeneration;

    public ProtectedWorkerCredentialProvider(IProtectedWorkerCredentialReader files, WorkerCredentialOptions options,
        TimeProvider clock, ILogger<ProtectedWorkerCredentialProvider> logger)
    {
        this.files = files; this.options = options; this.clock = clock; this.logger = logger;
        if (options.SubjectId == Guid.Empty
            || (options.Profile != WorkerCredentialEncoding.AdminAreaProfile && options.Profile != WorkerCredentialEncoding.ClientProfile)
            || options.OrganisationIds.Length == 0 || options.OrganisationIds.Length > 100
            || options.OrganisationIds.Any(x => x == Guid.Empty)
            || options.OrganisationIds.Distinct().Count() != options.OrganisationIds.Length
            || !Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(issuer.UserInfo) || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment)
            || string.IsNullOrWhiteSpace(options.Audience))
            throw new WorkerCredentialUnavailableException("WORKER_PROVIDER_CONFIGURATION_REJECTED");
    }

    public async ValueTask<WorkerCredentialSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        try
        {
            bytes = await files.ReadAsync(options.Directory, cancellationToken);
            var value = WorkerCredentialEncoding.Parse(bytes);
            var now = clock.GetUtcNow();
            if (value.Profile != options.Profile || value.SubjectId != options.SubjectId
                || !value.OrganisationIds.OrderBy(x => x).SequenceEqual(options.OrganisationIds.OrderBy(x => x))
                || value.TokenIssuedAtUtc > now || value.SessionCreatedAtUtc > now
                || value.AccessExpiresAtUtc <= now || value.SessionExpiresAtUtc <= now)
                throw new WorkerCredentialUnavailableException("WORKER_CREDENTIAL_SCOPE_OR_TIME_REJECTED");
            // This is metadata consistency only. Existing JWT validation and live Auth session/tenant
            // authority remain mandatory before worker persistence or publication.
            var handler = new JsonWebTokenHandler { MaximumTokenSizeInBytes = WorkerCredentialEncoding.MaximumBytes };
            if (!handler.CanReadToken(value.Token)) throw new WorkerCredentialUnavailableException("WORKER_TOKEN_METADATA_REJECTED");
            var jwt = handler.ReadJsonWebToken(value.Token);
            RequireUniqueProperties(jwt.EncodedHeader);
            RequireUniqueProperties(jwt.EncodedPayload);
            var claims = jwt.Claims.ToArray();
            bool One(string type, string expected) =>
                claims.Count(x => x.Type == type) == 1 && claims.Single(x => x.Type == type).Value == expected;
            if (jwt.Alg != "RS256" || string.IsNullOrWhiteSpace(jwt.Kid)
                || !One("sub", value.SubjectId.ToString("D")) || !One("sid", value.SessionId.ToString("D"))
                || !One("iss", options.Issuer) || jwt.Audiences.Count() != 1 || jwt.Audiences.Single() != options.Audience
                || claims.Count(x => x.Type == "iat") != 1 || claims.Count(x => x.Type == "exp") != 1
                || claims.Count(x => x.Type == "nbf") != 1 || jwt.ValidFrom != jwt.IssuedAt
                || new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero).ToUnixTimeSeconds() != value.TokenIssuedAtUtc.ToUnixTimeSeconds()
                || new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).ToUnixTimeSeconds() != value.AccessExpiresAtUtc.ToUnixTimeSeconds()
                || jwt.ValidTo <= jwt.IssuedAt || jwt.ValidTo - jwt.IssuedAt != TimeSpan.FromHours(1))
                throw new WorkerCredentialUnavailableException("WORKER_TOKEN_METADATA_REJECTED");
            lock (generationLock)
            {
                if (value.Generation < highestGeneration
                    || (value.Generation == highestGeneration && highestPayloadHash != value.PayloadSha256))
                    throw new WorkerCredentialUnavailableException("WORKER_GENERATION_ROLLBACK_REJECTED");
                highestGeneration = value.Generation;
                highestPayloadHash = value.PayloadSha256;
            }
            return new(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (WorkerCredentialUnavailableException) { throw; }
        catch { throw new WorkerCredentialUnavailableException("WORKER_CREDENTIAL_UNAVAILABLE"); }
        finally { if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireUniqueProperties(string encoded)
    {
        byte[]? decoded = null;
        try
        {
            decoded = Base64UrlEncoder.DecodeBytes(encoded);
            using var document = JsonDocument.Parse(decoded, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new WorkerCredentialUnavailableException("WORKER_TOKEN_METADATA_REJECTED");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (!names.Add(property.Name))
                    throw new WorkerCredentialUnavailableException("WORKER_TOKEN_METADATA_REJECTED");
        }
        finally
        {
            if (decoded is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public void MarkSuccessfulUse(WorkerCredentialSnapshot snapshot)
    {
        // Called only after existing live authority AND business-operation completion.
        if (snapshot.Profile != options.Profile || snapshot.SubjectId != options.SubjectId)
            throw new WorkerCredentialUnavailableException("WORKER_SUCCESS_CONTEXT_REJECTED");
        lock (successfulLock)
        {
            if (snapshot.Generation <= lastSuccessfulGeneration) return;
            lastSuccessfulGeneration = snapshot.Generation;
            logger.LogInformation("{WorkerCredentialAdoption}", JsonSerializer.Serialize(new
            {
                @event = "zeka-worker-credential-adopted", profile = snapshot.Profile,
                operationId = snapshot.OperationId, generation = snapshot.Generation,
                processId = Environment.ProcessId, processInstanceId = ProcessInstance,
                utc = clock.GetUtcNow()
            }));
        }
    }
}