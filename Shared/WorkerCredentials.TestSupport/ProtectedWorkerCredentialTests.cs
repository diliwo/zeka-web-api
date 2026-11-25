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
    [InlineData("missing-sub")]
    [InlineData("multi-audience")]
    [InlineData("malformed-format")]
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
        if (scenario == "missing-sub") payload.Remove("sub");
        if (scenario == "multi-audience") payload["aud"] = new JsonArray(WorkerCredentialFixture.Audience, "unadmitted-audience");
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
        if (scenario == "malformed-format") changed += ".unadmitted-segment";
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

public sealed class WorkerCredentialConfigurationTests
{
    private static readonly Guid Subject = Guid.NewGuid();
    private static readonly Guid Organisation = Guid.NewGuid();

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData(" true ")]
    public void Rotation_requires_the_explicit_configuration_value(string? enabled)
    {
        var configuration = Configuration();
        configuration["TenantWorker:CredentialRotationEnabled"] = enabled;
        var error = Assert.Throws<WorkerCredentialUnavailableException>(() =>
            WorkerCredentialOptions.Read(configuration, WorkerCredentialEncoding.ClientProfile));
        Assert.Equal("WORKER_ROTATION_NOT_ENABLED", error.Code);
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("subject")]
    [InlineData("empty-subject")]
    [InlineData("no-organisations")]
    [InlineData("too-many-organisations")]
    [InlineData("invalid-organisation")]
    [InlineData("empty-organisation")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public void Incomplete_scope_configuration_is_refused_before_reading_a_credential(string defect)
    {
        var values = Values();
        switch (defect)
        {
            case "directory": values["TenantWorker:CredentialDirectory"] = "/tmp/unadmitted"; break;
            case "subject": values["TenantWorker:SubjectId"] = "not-a-guid"; break;
            case "empty-subject": values["TenantWorker:SubjectId"] = Guid.Empty.ToString("D"); break;
            case "no-organisations": values.Remove("TenantWorker:OrganisationIds:0"); break;
            case "too-many-organisations":
                for (var index = 0; index <= 100; index++) values[$"TenantWorker:OrganisationIds:{index}"] = Guid.NewGuid().ToString("D");
                break;
            case "invalid-organisation": values["TenantWorker:OrganisationIds:0"] = "not-a-guid"; break;
            case "empty-organisation": values["TenantWorker:OrganisationIds:0"] = Guid.Empty.ToString("D"); break;
            case "issuer": values["Authentication:Issuer"] = " "; break;
            case "audience": values["Authentication:Audience"] = " "; break;
        }
        var error = Assert.Throws<WorkerCredentialUnavailableException>(() =>
            WorkerCredentialOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), WorkerCredentialEncoding.ClientProfile));
        Assert.Equal("WORKER_PROVIDER_CONFIGURATION_REJECTED", error.Code);
    }

    [Fact]
    public void Configuration_normalizes_only_organisation_order_and_duplicates()
    {
        var first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var values = Values();
        values["TenantWorker:CredentialRotationEnabled"] = "TRUE";
        values["TenantWorker:OrganisationIds:0"] = second.ToString("D");
        values["TenantWorker:OrganisationIds:1"] = first.ToString("D");
        values["TenantWorker:OrganisationIds:2"] = second.ToString("D");
        var options = WorkerCredentialOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), WorkerCredentialEncoding.ClientProfile);
        Assert.Equal(new[] { first, second }, options.OrganisationIds);
        Assert.Equal(Subject, options.SubjectId);
        Assert.Equal(WorkerCredentialOptions.MountedDirectory, options.Directory);
        Assert.Equal(WorkerCredentialEncoding.ClientProfile, options.Profile);
        Assert.Equal(WorkerCredentialFixture.Issuer, options.Issuer);
        Assert.Equal(WorkerCredentialFixture.Audience, options.Audience);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("profile")]
    [InlineData("no-organisations")]
    [InlineData("too-many-organisations")]
    [InlineData("empty-organisation")]
    [InlineData("duplicate-organisation")]
    [InlineData("relative-issuer")]
    [InlineData("http-issuer")]
    [InlineData("issuer-user-info")]
    [InlineData("issuer-query")]
    [InlineData("issuer-fragment")]
    [InlineData("audience")]
    public void Provider_refuses_an_invalid_scope_even_when_options_bypass_configuration_binding(string defect)
    {
        var options = Options(defect);
        var reader = new NeverRead();
        var error = Assert.Throws<WorkerCredentialUnavailableException>(() => new ProtectedWorkerCredentialProvider(
            reader, options, TimeProvider.System, NullLogger<ProtectedWorkerCredentialProvider>.Instance));
        Assert.Equal("WORKER_PROVIDER_CONFIGURATION_REJECTED", error.Code);
        Assert.Equal(0, reader.Reads);
    }

    [Theory]
    [InlineData(WorkerCredentialEncoding.AdminAreaProfile)]
    [InlineData(WorkerCredentialEncoding.ClientProfile)]
    public void Both_admitted_profiles_accept_the_exact_scope_without_reading_material(string profile)
    {
        var reader = new NeverRead();
        var options = Options(profile: profile);
        _ = new ProtectedWorkerCredentialProvider(reader, options, TimeProvider.System, NullLogger<ProtectedWorkerCredentialProvider>.Instance);
        Assert.Equal(0, reader.Reads);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_without_a_redacted_fallback_exception()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var expected = new OperationCanceledException(cancellation.Token);
        var provider = WorkerCredentialFixture.Provider(new ThrowingReader(expected), [Organisation]);
        var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ReadAsync(cancellation.Token).AsTask());
        Assert.Same(expected, observed);
        Assert.Equal(cancellation.Token, observed.CancellationToken);
    }

    [Fact]
    public async Task Unrequested_reader_cancellation_is_unavailable_and_does_not_export_the_original_error()
    {
        var provider = WorkerCredentialFixture.Provider(new ThrowingReader(new OperationCanceledException()), [Organisation]);
        var error = await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => provider.ReadAsync(default).AsTask());
        Assert.Equal("WORKER_CREDENTIAL_UNAVAILABLE", error.Code);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Credential_buffer_is_cleared_after_a_valid_snapshot_is_created()
    {
        var package = WorkerCredentialFixture.Package([Organisation]);
        var bytes = WorkerCredentialEncoding.Serialize(package);
        var provider = WorkerCredentialFixture.Provider(new BorrowedBytes(bytes), [Organisation]);
        var snapshot = await provider.ReadAsync(default);
        Assert.All(bytes, value => Assert.Equal((byte)0, value));
        Assert.Equal(package.OperationId, snapshot.OperationId);
        Assert.Equal(package.Generation, snapshot.Generation);
        Assert.Equal(WorkerCredentialFixture.Subject, snapshot.SubjectId);
        Assert.Equal(package.SessionId, snapshot.SessionId);
        Assert.Equal(package.Profile, snapshot.Profile);
        Assert.Equal(new[] { Organisation }, snapshot.OrganisationIds);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)snapshot.OrganisationIds)[0] = Guid.NewGuid());
        Assert.Equal(package.TokenIssuedAtUtc, snapshot.TokenIssuedAtUtc);
        Assert.Equal(package.AccessExpiresAtUtc, snapshot.AccessExpiresAtUtc);
        Assert.Equal(package.SessionCreatedAtUtc, snapshot.SessionCreatedAtUtc);
        Assert.Equal(package.SessionExpiresAtUtc, snapshot.SessionExpiresAtUtc);
        Assert.Equal(package.PayloadSha256, snapshot.PayloadSha256);
        Assert.Equal(WorkerCredentialFixture.TokenSha256(package.Token), WorkerCredentialFixture.TokenSha256(snapshot.Token));
        Assert.Equal("Worker credential snapshot (redacted)", snapshot.ToString());
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("subject")]
    public async Task Adoption_from_another_scope_is_refused_before_any_success_log(string difference)
    {
        var foreign = difference == "profile" ? WorkerCredentialFixture.Provider(new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(
            WorkerCredentialFixture.Package([Organisation], profile: WorkerCredentialEncoding.ClientProfile))),
            [Organisation], WorkerCredentialEncoding.ClientProfile)
            : new ProtectedWorkerCredentialProvider(new WorkerCredentialFixture.Bytes(WorkerCredentialEncoding.Serialize(
                WorkerCredentialFixture.Package([Organisation], subject: Subject))), Options(),
                new WorkerCredentialFixture.Clock(WorkerCredentialFixture.Now.AddMinutes(1)),
                NullLogger<ProtectedWorkerCredentialProvider>.Instance);
        var snapshot = await foreign.ReadAsync(default);
        var logs = new AdoptionLogs();
        var target = WorkerCredentialFixture.Provider(new NeverRead(), [Organisation], logger: logs);
        Assert.Empty(logs.Entries);
        var error = Assert.Throws<WorkerCredentialUnavailableException>(() => target.MarkSuccessfulUse(snapshot));
        Assert.Equal("WORKER_SUCCESS_CONTEXT_REJECTED", error.Code);
        Assert.Empty(logs.Entries);
    }

    private static ConfigurationManager Configuration()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(Values());
        return configuration;
    }

    private static Dictionary<string, string?> Values() => new()
    {
        ["TenantWorker:CredentialRotationEnabled"] = "true",
        ["TenantWorker:CredentialDirectory"] = WorkerCredentialOptions.MountedDirectory,
        ["TenantWorker:SubjectId"] = Subject.ToString("D"),
        ["TenantWorker:OrganisationIds:0"] = Organisation.ToString("D"),
        ["Authentication:Issuer"] = WorkerCredentialFixture.Issuer,
        ["Authentication:Audience"] = WorkerCredentialFixture.Audience
    };

    private static WorkerCredentialOptions Options(string? defect = null, string profile = WorkerCredentialEncoding.AdminAreaProfile) => new()
    {
        SubjectId = defect == "subject" ? Guid.Empty : Subject,
        Profile = defect == "profile" ? "unadmitted" : profile,
        OrganisationIds = defect switch
        {
            "no-organisations" => [],
            "too-many-organisations" => Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray(),
            "empty-organisation" => [Guid.Empty],
            "duplicate-organisation" => [Organisation, Organisation],
            _ => [Organisation]
        },
        Directory = WorkerCredentialOptions.MountedDirectory,
        Issuer = defect switch
        {
            "relative-issuer" => "/auth",
            "http-issuer" => "http://auth.invalid/",
            "issuer-user-info" => "https://operator@auth.invalid/",
            "issuer-query" => "https://auth.invalid/?unexpected=1",
            "issuer-fragment" => "https://auth.invalid/#unexpected",
            _ => WorkerCredentialFixture.Issuer
        },
        Audience = defect == "audience" ? " " : WorkerCredentialFixture.Audience
    };

    private sealed class NeverRead : IProtectedWorkerCredentialReader
    {
        internal int Reads;
        public ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Configuration must be admitted before reading.");
        }
    }

    private sealed class ThrowingReader(Exception error) : IProtectedWorkerCredentialReader
    {
        public ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken) => throw error;
    }

    private sealed class BorrowedBytes(byte[] value) : IProtectedWorkerCredentialReader
    {
        public ValueTask<byte[]> ReadAsync(string directory, CancellationToken cancellationToken) => ValueTask.FromResult(value);
    }

    private sealed class AdoptionLogs : ILogger<ProtectedWorkerCredentialProvider>
    {
        internal readonly List<EventId> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add(eventId);
    }
}
