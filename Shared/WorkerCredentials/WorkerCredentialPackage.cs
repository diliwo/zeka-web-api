using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zeka.WorkerCredentials
{
    [DebuggerDisplay("Protected worker credential (redacted)")]
    public sealed class WorkerCredentialPackage
    {
        public string Schema { get; set; } = WorkerCredentialEncoding.Schema;
        public Guid OperationId { get; set; }
        public long Generation { get; set; }
        public Guid SubjectId { get; set; }
        public Guid SessionId { get; set; }
        public string Profile { get; set; } = "";
        public Guid[] OrganisationIds { get; set; } = Array.Empty<Guid>();
        public DateTimeOffset TokenIssuedAtUtc { get; set; }
        public DateTimeOffset AccessExpiresAtUtc { get; set; }
        public DateTimeOffset SessionCreatedAtUtc { get; set; }
        public DateTimeOffset SessionExpiresAtUtc { get; set; }
        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        public string Token { get; set; } = "";
        public string PayloadSha256 { get; set; } = "";
        public override string ToString() { return "Protected worker credential (redacted)"; }
    }

    public static class WorkerCredentialEncoding
    {
        public const string Schema = "zeka.m1.worker-credential/v1";
        public const string FileName = "current.json";
        public const int MaximumBytes = 32768;
        public const string AdminAreaProfile = "adminarea-retry";
        public const string ClientProfile = "client-projection";
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 4
        };
        public static WorkerCredentialPackage Create(Guid operationId, long generation, Guid subjectId,
            Guid sessionId, string profile, IEnumerable<Guid> organisationIds,
            DateTimeOffset tokenIssuedAtUtc, DateTimeOffset accessExpiresAtUtc,
            DateTimeOffset sessionCreatedAtUtc, DateTimeOffset sessionExpiresAtUtc, string token)
        {
            var package = new WorkerCredentialPackage
            {
                OperationId = operationId, Generation = generation, SubjectId = subjectId,
                SessionId = sessionId, Profile = profile,
                OrganisationIds = organisationIds.OrderBy(x => x).ToArray(),
                TokenIssuedAtUtc = tokenIssuedAtUtc.ToUniversalTime(),
                AccessExpiresAtUtc = accessExpiresAtUtc.ToUniversalTime(),
                SessionCreatedAtUtc = sessionCreatedAtUtc.ToUniversalTime(),
                SessionExpiresAtUtc = sessionExpiresAtUtc.ToUniversalTime(), Token = token
            };
            ValidateShape(package);
            package.PayloadSha256 = Hash(package);
            return package;
        }
        public static byte[] Serialize(WorkerCredentialPackage package)
        {
            ValidateShape(package);
            if (!ValidDigest(package)) throw new InvalidOperationException("WORKER_PAYLOAD_HASH_REJECTED");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(package, JsonOptions);
            if (bytes.Length > MaximumBytes) throw new InvalidOperationException("WORKER_PAYLOAD_SIZE_REJECTED");
            return bytes;
        }
        public static WorkerCredentialPackage Parse(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumBytes)
                throw new InvalidOperationException("WORKER_PAYLOAD_SIZE_REJECTED");
            try
            {
                using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }))
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException("WORKER_PAYLOAD_SCHEMA_REJECTED");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in document.RootElement.EnumerateObject())
                        if (!names.Add(item.Name)) throw new InvalidOperationException("WORKER_PAYLOAD_SCHEMA_REJECTED");
                }
                var value = JsonSerializer.Deserialize<WorkerCredentialPackage>(bytes, JsonOptions);
                if (value == null) throw new InvalidOperationException("WORKER_PAYLOAD_SCHEMA_REJECTED");
                ValidateShape(value);
                if (!ValidDigest(value)) throw new InvalidOperationException("WORKER_PAYLOAD_HASH_REJECTED");
                return value;
            }
            catch (JsonException) { throw new InvalidOperationException("WORKER_PAYLOAD_SCHEMA_REJECTED"); }
        }
        private static bool ValidDigest(WorkerCredentialPackage value)
        {
            if (value.PayloadSha256 == null || value.PayloadSha256.Length != 64 || value.PayloadSha256.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))) return false;
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(value.PayloadSha256), Convert.FromHexString(Hash(value)));
        }
        private static string Hash(WorkerCredentialPackage value)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("schema", value.Schema);
                    writer.WriteString("operationId", value.OperationId.ToString("D"));
                    writer.WriteNumber("generation", value.Generation);
                    writer.WriteString("subjectId", value.SubjectId.ToString("D"));
                    writer.WriteString("sessionId", value.SessionId.ToString("D"));
                    writer.WriteString("profile", value.Profile);
                    writer.WriteStartArray("organisationIds");
                    foreach (var id in value.OrganisationIds.OrderBy(x => x)) writer.WriteStringValue(id.ToString("D"));
                    writer.WriteEndArray();
                    // UTC ticks preserve every deadline; wire JSON whitespace/date spelling is irrelevant.
                    writer.WriteNumber("tokenIssuedAtUtcTicks", value.TokenIssuedAtUtc.UtcTicks);
                    writer.WriteNumber("accessExpiresAtUtcTicks", value.AccessExpiresAtUtc.UtcTicks);
                    writer.WriteNumber("sessionCreatedAtUtcTicks", value.SessionCreatedAtUtc.UtcTicks);
                    writer.WriteNumber("sessionExpiresAtUtcTicks", value.SessionExpiresAtUtc.UtcTicks);
                    writer.WriteString("token", value.Token);
                    writer.WriteEndObject();
                }
                return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
            }
        }
        private static void ValidateShape(WorkerCredentialPackage value)
        {
            if (value.Schema != Schema || value.OperationId == Guid.Empty || value.Generation <= 0
                || value.SubjectId == Guid.Empty || value.SessionId == Guid.Empty
                || (value.Profile != AdminAreaProfile && value.Profile != ClientProfile)
                || value.OrganisationIds == null || value.OrganisationIds.Length == 0 || value.OrganisationIds.Length > 100
                || value.OrganisationIds.Any(x => x == Guid.Empty) || value.OrganisationIds.Distinct().Count() != value.OrganisationIds.Length
                || string.IsNullOrEmpty(value.Token) || value.Token.Length > 24576
                || value.TokenIssuedAtUtc == default || value.AccessExpiresAtUtc <= value.TokenIssuedAtUtc
                || value.AccessExpiresAtUtc - value.TokenIssuedAtUtc != TimeSpan.FromHours(1)
                || value.SessionCreatedAtUtc == default || value.SessionExpiresAtUtc - value.SessionCreatedAtUtc != TimeSpan.FromHours(1)
                || value.TokenIssuedAtUtc.Offset != TimeSpan.Zero || value.AccessExpiresAtUtc.Offset != TimeSpan.Zero
                || value.SessionCreatedAtUtc.Offset != TimeSpan.Zero || value.SessionExpiresAtUtc.Offset != TimeSpan.Zero)
                throw new InvalidOperationException("WORKER_PAYLOAD_SCHEMA_REJECTED");
        }
    }
}