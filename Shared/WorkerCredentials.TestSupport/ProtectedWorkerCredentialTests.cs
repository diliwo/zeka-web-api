using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Zeka.WorkerCredentials;

namespace Zeka.WorkerCredentials.Testing;

// Synthetic cryptographic material is generated in memory and is never written to a fixture file.
internal static class WorkerCredentialFixture
{
    internal static readonly Guid Subject = Guid.NewGuid();
    internal const string Issuer = "https://auth.invalid/";
    internal const string Audience = "zeka-m1-worker-tests";
    internal static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793628000);
    internal static WorkerCredentialPackage Package(IEnumerable<Guid> organisations, long generation = 1,
        string profile = WorkerCredentialEncoding.AdminAreaProfile, DateTimeOffset? issued = null, Guid? subject = null)
    {
        var at = issued ?? Now;
        var owner = subject ?? Subject;
        var session = Guid.NewGuid();
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = Guid.NewGuid().ToString("N") };
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer, Audience = Audience,
            Claims = new Dictionary<string, object> { ["sub"] = owner.ToString("D"), ["sid"] = session.ToString("D") },
            IssuedAt = at.UtcDateTime, NotBefore = at.UtcDateTime, Expires = at.AddHours(1).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
        return WorkerCredentialEncoding.Create(Guid.NewGuid(), generation, owner, session, profile, organisations,
            at, at.AddHours(1), at.AddTicks(1230), at.AddTicks(1230).AddHours(1), token);
    }
    internal static ProtectedWorkerCredentialProvider Provider(IProtectedWorkerCredentialReader reader,
        IEnumerable<Guid> organisations, string profile = WorkerCredentialEncoding.AdminAreaProfile,
        TimeProvider? clock = null, ILogger<ProtectedWorkerCredentialProvider>? logger = null) => new(reader,
        new WorkerCredentialOptions { Directory = WorkerCredentialOptions.MountedDirectory, SubjectId = Subject,
            OrganisationIds = organisations.ToArray(), Profile = profile, Issuer = Issuer, Audience = Audience },
        clock ?? new Clock(Now.AddMinutes(1)), logger ?? NullLogger<ProtectedWorkerCredentialProvider>.Instance);
    internal static IWorkerCredentialProvider For(IEnumerable<Guid> organisations, string profile)
    {
        var ids = organisations.ToArray();
        return Provider(new Bytes(WorkerCredentialEncoding.Serialize(Package(ids, profile: profile,
            issued: DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds())))), ids,
            profile, TimeProvider.System);
    }
    internal static string TokenSha256(string? token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));
    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    internal sealed class Bytes(byte[] value) : IProtectedWorkerCredentialReader
    {
        internal byte[] Value = value;
        internal int Reads;
        public ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Value.ToArray());
        }
    }
}

public sealed class ProtectedWorkerCredentialTests
{
    private readonly Guid organisation = Guid.NewGuid();

    [Fact]
    public async Task Same_provider_observes_new_generation_without_restart_and_never_caches_token()
    {
        var first = WorkerCredentialFixture.Package([organisation]);
        var reader = new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(first));
        var provider = WorkerCredentialFixture.Provider(reader, [organisation]);
        var oldSnapshot = await provider.ReadAsync(default);
        reader.Value = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation], 2));
        var next = await provider.ReadAsync(default);
        Assert.Equal(2, next.Generation);
        Assert.Equal(1, oldSnapshot.Generation);
        Assert.True(oldSnapshot.PayloadSha256 != next.PayloadSha256);
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Earlier_inflight_snapshot_remains_immutable_but_new_reads_reject_rollback()
    {
        var first = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation]));
        var reader = new WorkerCredentialFixture.Bytes(first);
        var provider = WorkerCredentialFixture.Provider(reader, [organisation]);
        var oldSnapshot = await provider.ReadAsync(default);
        reader.Value = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation], 2));
        var newest = await provider.ReadAsync(default);
        reader.Value = first;
        var failure = await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => provider.ReadAsync(default).AsTask());
        Assert.Equal("WORKER_GENERATION_ROLLBACK_REJECTED", failure.Code);
        Assert.Equal(1, oldSnapshot.Generation);
        Assert.Equal(2, newest.Generation);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("subject")]
    [InlineData("scope")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("hash")]
    [InlineData("partial")]
    [InlineData("empty")]
    [InlineData("same-generation")]
    public async Task Invalid_or_changed_package_fails_closed(string scenario)
    {
        var reader = new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation])));
        var provider = WorkerCredentialFixture.Provider(reader, [organisation]);
        if (scenario == "same-generation") await provider.ReadAsync(default);
        var value = scenario switch
        {
            "profile" => WorkerCredentialFixture.Package([organisation], profile: WorkerCredentialEncoding.ClientProfile),
            "subject" => WorkerCredentialFixture.Package([organisation], subject: Guid.NewGuid()),
            "scope" => WorkerCredentialFixture.Package([Guid.NewGuid()]),
            "expired" => WorkerCredentialFixture.Package([organisation], issued: WorkerCredentialFixture.Now.AddHours(-2)),
            "future" => WorkerCredentialFixture.Package([organisation], issued: WorkerCredentialFixture.Now.AddMinutes(2)),
            _ => WorkerCredentialFixture.Package([organisation])
        };
        var bytes = WorkerCredentialEncoding.Serialize(value);
        if (scenario == "hash") bytes[Array.IndexOf(bytes, (byte)'1')] = (byte)'9';
        if (scenario == "partial") bytes = bytes[..(bytes.Length / 2)];
        if (scenario == "empty") bytes = [];
        reader.Value = bytes;
        await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => provider.ReadAsync(default).AsTask());
    }

    [Fact]
    public async Task Unreadable_input_has_redacted_failure_and_no_success_fallback()
    {
        var provider = WorkerCredentialFixture.Provider(new Unreadable(), [organisation]);
        var error = await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => provider.ReadAsync(default).AsTask());
        Assert.Equal("WORKER_CREDENTIAL_UNAVAILABLE", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Stored_session_and_JWT_duration_are_independently_exactly_one_hour()
    {
        var value = WorkerCredentialFixture.Package([organisation]);
        Assert.Equal(TimeSpan.FromHours(1), value.SessionExpiresAtUtc - value.SessionCreatedAtUtc);
        Assert.Equal(TimeSpan.FromHours(1), value.AccessExpiresAtUtc - value.TokenIssuedAtUtc);
        Assert.True(value.SessionExpiresAtUtc > value.AccessExpiresAtUtc);
        value.SessionExpiresAtUtc = value.SessionExpiresAtUtc.AddTicks(1);
        Assert.Throws<InvalidOperationException>(() => WorkerCredentialEncoding.Serialize(value));
    }

    [Theory]
    [InlineData("session-created")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("oversize")]
    public void Encoding_rejects_missing_or_ambiguous_fields(string scenario)
    {
        var value = WorkerCredentialFixture.Package([organisation]);
        var bytes = WorkerCredentialEncoding.Serialize(value);
        var text = Encoding.UTF8.GetString(bytes);
        bytes = scenario switch
        {
            "session-created" => Encoding.UTF8.GetBytes(text.Replace("\"sessionCreatedAtUtc\":", "\"missingCreated\":")),
            "duplicate" => Encoding.UTF8.GetBytes("{\"schema\":\"duplicate\"," + text[1..]),
            "unknown" => Encoding.UTF8.GetBytes("{\"unexpected\":true," + text[1..]),
            _ => new byte[WorkerCredentialEncoding.MaximumBytes + 1]
        };
        Assert.Throws<InvalidOperationException>(() => WorkerCredentialEncoding.Parse(bytes));
    }

    [Fact]
    public async Task Adoption_is_emitted_only_on_success_once_per_generation_with_no_subject_scope_or_token()
    {
        var logs = new LogCapture();
        var reader = new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation])));
        var provider = WorkerCredentialFixture.Provider(reader, [organisation], logger: logs);
        var first = await provider.ReadAsync(default);
        Assert.Empty(logs.Messages);
        provider.MarkSuccessfulUse(first);
        provider.MarkSuccessfulUse(first);
        reader.Value = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation], 2));
        var second = await provider.ReadAsync(default);
        provider.MarkSuccessfulUse(second);
        Assert.Equal(2, logs.Messages.Count);
        var identities = new List<Guid>();
        foreach (var line in logs.Messages)
        {
            using var parsed = JsonDocument.Parse(line);
            var root = parsed.RootElement;
            Assert.Equal("zeka-worker-credential-adopted", root.GetProperty("event").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("processId").GetInt32());
            Assert.Equal(7, root.EnumerateObject().Count());
            identities.Add(root.GetProperty("processInstanceId").GetGuid());
            Assert.False(line.Contains(first.Token, StringComparison.Ordinal));
            Assert.False(line.Contains(WorkerCredentialFixture.Subject.ToString("D"), StringComparison.Ordinal));
            Assert.False(line.Contains(organisation.ToString("D"), StringComparison.Ordinal));
        }
        Assert.Equal(identities[0], identities[1]);
    }

    [Fact]
    public void Rotation_is_explicitly_default_off_and_static_input_never_falls_back()
    {
        var configuration = new ConfigurationManager();
        Assert.Equal("WORKER_ROTATION_NOT_ENABLED", Assert.Throws<WorkerCredentialUnavailableException>(
            () => WorkerCredentialOptions.Read(configuration, WorkerCredentialEncoding.AdminAreaProfile)).Code);
        configuration["TenantWorker:CredentialRotationEnabled"] = "true";
        configuration["TenantWorker:BearerToken"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Assert.Equal("STATIC_WORKER_TOKEN_REJECTED", Assert.Throws<WorkerCredentialUnavailableException>(
            () => WorkerCredentialOptions.Read(configuration, WorkerCredentialEncoding.AdminAreaProfile)).Code);
    }

    [Fact]
    public async Task Production_reader_has_no_Windows_or_unprivileged_fallback()
    {
        if (!OperatingSystem.IsWindows()) return; // Linux permission conformance requires its real native fixture.
        await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() =>
            new LinuxProtectedWorkerCredentialReader().ReadAsync(WorkerCredentialOptions.MountedDirectory, default).AsTask());
    }

    [Fact]
    public async Task Concurrent_old_read_cannot_complete_after_new_generation_is_admitted()
    {
        var oldBytes = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation]));
        var newBytes = WorkerCredentialEncoding.Serialize(WorkerCredentialFixture.Package([organisation], 2));
        var reader = new DelayedFirst(newBytes);
        var provider = WorkerCredentialFixture.Provider(reader, [organisation]);
        var oldRead = provider.ReadAsync(default).AsTask();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = await provider.ReadAsync(default);
        reader.Release.SetResult(oldBytes);
        var failure = await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => oldRead);
        Assert.Equal("WORKER_GENERATION_ROLLBACK_REJECTED", failure.Code);
        Assert.Equal(2, next.Generation);
    }

    [Theory]
    [InlineData("sid")]
    [InlineData("iss")]
    [InlineData("aud")]
    [InlineData("iat")]
    [InlineData("exp")]
    [InlineData("nbf")]
    [InlineData("alg")]
    [InlineData("kid")]
    [InlineData("duplicate-header")]
    [InlineData("duplicate-payload")]
    public async Task Signed_token_metadata_must_match_package_without_replacing_real_signature_validation(string scenario)
    {
        var original = WorkerCredentialFixture.Package([organisation]);
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(original.Token);
        var header = JsonNode.Parse(Base64UrlEncoder.Decode(jwt.EncodedHeader))!.AsObject();
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload))!.AsObject();
        if (scenario == "sid") payload["sid"] = Guid.NewGuid().ToString("D");
        if (scenario == "iss") payload["iss"] = "https://other.invalid/";
        if (scenario == "aud") payload["aud"] = "another-audience";
        if (scenario == "iat") payload["iat"] = original.TokenIssuedAtUtc.AddSeconds(1).ToUnixTimeSeconds();
        if (scenario == "exp") payload["exp"] = original.AccessExpiresAtUtc.AddSeconds(1).ToUnixTimeSeconds();
        if (scenario == "nbf") payload["nbf"] = original.TokenIssuedAtUtc.AddSeconds(1).ToUnixTimeSeconds();
        if (scenario == "alg") header["alg"] = "HS256";
        if (scenario == "kid") header["kid"] = "";
        var headerText = header.ToJsonString();
        var payloadText = payload.ToJsonString();
        if (scenario == "duplicate-header") headerText = "{\"alg\":\"RS256\"," + headerText[1..];
        if (scenario == "duplicate-payload") payloadText = "{\"sub\":\"ambiguous\"," + payloadText[1..];
        var changed = Base64UrlEncoder.Encode(headerText) + "." + Base64UrlEncoder.Encode(payloadText) + "." + jwt.EncodedSignature;
        var value = WorkerCredentialEncoding.Create(original.OperationId, original.Generation, original.SubjectId,
            original.SessionId, original.Profile, original.OrganisationIds, original.TokenIssuedAtUtc,
            original.AccessExpiresAtUtc, original.SessionCreatedAtUtc, original.SessionExpiresAtUtc, changed);
        var provider = WorkerCredentialFixture.Provider(new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(value)), [organisation]);
        await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => provider.ReadAsync(default).AsTask());
    }

    private sealed class DelayedFirst(byte[] second) : IProtectedWorkerCredentialReader
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<byte[]> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int reads;
        public async ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                Entered.SetResult();
                return await Release.Task.WaitAsync(cancellationToken);
            }
            return second.ToArray();
        }
    }

    private sealed class Unreadable : IProtectedWorkerCredentialReader
    {
        public ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken) => throw new IOException();
    }
    private sealed class LogCapture : ILogger<ProtectedWorkerCredentialProvider>
    {
        internal readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
