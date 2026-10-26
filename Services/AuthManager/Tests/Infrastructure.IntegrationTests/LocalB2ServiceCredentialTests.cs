using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthManager.API;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace Zeka.LocalB2Prerequisites;

/// <summary>
/// Explicitly selected local TEST operations. A normal suite does not acquire a target,
/// read private inputs, start a listener or invoke Docker. Fixed failure codes deliberately
/// replace raw exceptions, captured bytes and assertion diagnostics.
/// </summary>
[Trait("Issue", "97")]
public sealed class LocalB2ServiceCredentialTests
{
    private const string Source = "198a66015a5a90e2936f3caa263774d74011d1ec";
    private const string Marker = "ZEKA-B2-O5-NONSECRET-MARKER";
    private const string Kid = "zeka-b2-local-service";
    private const string ReadCommand = "exec 2>/dev/null; test -f /run/zeka-local/service-token && test -r /run/zeka-local/service-token || exit 42; exec head -c 8193 /run/zeka-local/service-token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false };

    [Fact] public Task PrepareTrust() => Selected(nameof(PrepareTrust), Prepare);
    [Fact] public Task RunProducer() => Selected(nameof(RunProducer), Produce);
    [Fact] public Task VerifyAuthority() => Selected(nameof(VerifyAuthority), Authority);
    [Fact] public Task VerifyDelivery() => Selected(nameof(VerifyDelivery), Delivery);
    [Fact] public Task StopAndClean() => Selected(nameof(StopAndClean), Stop);

    [Fact]
    public void Activation_requires_both_explicit_manifest_and_exact_operation()
    {
        Assert.False(IsSelected(null, null, "PrepareTrust"));
        Assert.False(IsSelected("C:/manifest.json", null, "PrepareTrust"));
        Assert.False(IsSelected(null, "PrepareTrust", "PrepareTrust"));
        Assert.False(IsSelected("C:/manifest.json", "RunProducer", "PrepareTrust"));
        Assert.True(IsSelected("C:/manifest.json", "PrepareTrust", "PrepareTrust"));
    }

    [Fact]
    public void Fixed_failures_do_not_carry_private_input_or_inner_exception()
    {
        var error = new H1Failure("H1_CAPTURE_FAILED");
        Assert.Equal("H1_CAPTURE_FAILED", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(8192, true)]
    [InlineData(8193, false)]
    public void Token_size_contract_is_closed(int count, bool admitted) =>
        Assert.Equal(admitted, ValidTokenLength(count));

    [Fact]
    public void Unreadable_policy_adds_only_explicit_ReadData_denials_and_restores_descriptor()
    {
        if (!OperatingSystem.IsWindows()) return;
        WindowsAclPolicyContract();
    }

    [SupportedOSPlatform("windows")]
    private static void WindowsAclPolicyContract()
    {
        var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var policy = new FileSecurity();
        policy.SetAccessRuleProtection(true, false);
        policy.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Read,
            AccessControlType.Allow));
        var original = policy.GetSecurityDescriptorBinaryForm();
        try
        {
            policy.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            policy.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.ReadData,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            var rules = policy.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Assert.True(HasReadDenial(rules, sid));
            Assert.True(HasReadDenial(rules, system));
            Assert.All(rules.Where(rule => rule.AccessControlType == AccessControlType.Deny),
                rule => Assert.Equal(FileSystemRights.ReadData, rule.FileSystemRights));
            var restored = new FileSecurity();
            restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
            Assert.Equal(original, restored.GetSecurityDescriptorBinaryForm());
        }
        finally { CryptographicOperations.ZeroMemory(original); }
    }

    private static bool IsSelected(string? manifest, string? operation, string expected) =>
        !string.IsNullOrWhiteSpace(manifest) &&
        string.Equals(operation, expected, StringComparison.Ordinal);

    private static bool ValidTokenLength(int count) => count is > 0 and <= 8192;

    private static async Task Selected(string operation, Func<Run, Task> body)
    {
        var manifest = Environment.GetEnvironmentVariable("ZEKA_B2_MANIFEST");
        if (!IsSelected(manifest, Environment.GetEnvironmentVariable("ZEKA_B2_OPERATION"), operation))
            return;
        try
        {
            using var run = Run.Load(manifest!);
            await body(run);
            Console.WriteLine("H1_" + operation + "_OK");
        }
        catch (H1Failure failure)
        {
            throw new InvalidOperationException(failure.Message);
        }
        catch
        {
            throw new InvalidOperationException("H1_" + operation + "_FAILED");
        }
    }

    private static async Task Prepare(Run run)
    {
        run.CheckTokenDirectory();
        Demand(!File.Exists(run.StopFile) && !File.Exists(run.TokenFile), "H1_OWNERSHIP_COLLISION");
        await AtomicWrite(run.TokenFile, Encoding.ASCII.GetBytes(Marker), run);
        string? id = null;
        try
        {
            using var created = await Docker(run,
                "container", "create", "--name", run.ProbeName,
                "--label", "zeka.b2.run=" + run.RunId, "--pull", "never",
                "--log-driver", "none", "--network", "none", "--read-only",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges=true",
                "--no-healthcheck", "--restart", "no", "--user", run.ProbeUser,
                "--mount", "type=bind,source=" + run.TokenDirectory + ",target=/run/zeka-local,readonly",
                "--entrypoint", "/bin/sh", run.ImageId, "-c", ReadCommand);
            Demand(created.ExitCode == 0 && created.Error.Length == 0, "H1_PROBE_CREATE");
            id = SafeAscii(created.Output).Trim();
            Demand(IsContainerId(id), "H1_PROBE_ID");
            await Inspect(run, id, first: true);
            using (var read = await Docker(run, "container", "start", "--attach", id))
            {
                Demand(read.ExitCode == 0 && read.Error.Length == 0 &&
                    read.Output.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Marker)), "H1_MARKER_CAPTURE");
            }
            await InspectExit(run, id, 0);
            await Inspect(run, id, first: false);
            using (var logs = await Docker(run, "container", "logs", id))
            {
                var empty = logs.ExitCode == 0 && logs.Output.Length == 0 && logs.Error.Length == 0;
                var unsupported = logs.Output.Length == 0 && logs.ExitCode != 0 &&
                    IsExplicitNoneLogsFailure(SafeAscii(logs.Error));
                Demand(empty || unsupported, "H1_LOGGING_GATE");
            }
            using var signing = run.SigningKey();
            using var certificate = run.ProducerCertificate();
            using var ca = run.RootCertificate();
            Demand(signing.KeySize >= 2048 && certificate.HasPrivateKey, "H1_KEY_CONTRACT");
            Demand(certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow, "H1_CERTIFICATE_EXPIRED");
            await WriteState(run, new State { ProbeId = id, MarkerPassed = true, Producer = "not-started" });
            Console.WriteLine("H1_MARKER_LOGGING_GATE_PASSED");
        }
        catch
        {
            if (id is not null) await RemoveProbe(run, id);
            RemoveOwnedFile(run, run.TokenFile);
            RemoveOwnedFile(run, run.StopFile);
            throw;
        }
    }

    private static async Task Produce(Run run)
    {
        var state = ReadState(run);
        Demand(state.MarkerPassed && IsContainerId(state.ProbeId), "H1_MARKER_REQUIRED");
        await Inspect(run, state.ProbeId, first: false);
        Demand(File.ReadAllBytes(run.TokenFile).AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Marker)),
            "H1_MARKER_STATE");
        using var key = run.SigningKey();
        using var certificate = run.ProducerCertificate();
        var publicKey = key.ExportParameters(false);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = Kid,
                n = Base64UrlEncoder.Encode(publicKey.Modulus), e = Base64UrlEncoder.Encode(publicKey.Exponent) } }
        });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { EnvironmentName = "Development", Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 19445,
            listener => listener.UseHttps(certificate)));
        await using var app = builder.Build();
        app.MapGet("/.well-known/jwks.json", () => Microsoft.AspNetCore.Http.Results.Text(jwks, "application/json"));
        await app.StartAsync();
        var bound = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()?.Addresses.ToArray() ?? [];
        Demand(bound.Length == 1 && bound[0] == "https://127.0.0.1:19445", "H1_PRODUCER_BINDING");
        await WriteState(run, state with { Producer = "listening" });
        var started = DateTimeOffset.UtcNow;
        var next = DateTimeOffset.MinValue;
        try
        {
            while (DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(150))
            {
                if (StopRequested(run)) break;
                if (!DeliveryAdmitted(run))
                {
                    await Task.Delay(250);
                    continue;
                }
                if (DateTimeOffset.UtcNow >= next)
                {
                    var bytes = Encoding.ASCII.GetBytes(Token(run, key, "valid"));
                    try { await AtomicWrite(run.TokenFile, bytes, run); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                    next = DateTimeOffset.UtcNow.AddSeconds(240);
                    // Do not overwrite a concurrent stop request during renewal.
                }
                await Task.Delay(250);
            }
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(stop.Token);
            await WriteState(run, state with { Producer = "stopped" });
        }
    }

    private static async Task Authority(Run run)
    {
        var state = ReadState(run);
        Demand(state.MarkerPassed && !state.ProbeRemoved && IsContainerId(state.ProbeId) &&
            state.Producer == "listening", "H1_MARKER_REQUIRED");
        await Inspect(run, state.ProbeId, first: false);
        Demand(DeliveryAdmitted(run), "H1_DELIVERY_ADMISSION_REQUIRED");
        using var key = run.SigningKey();
        using var client = run.AuthClient();
        foreach (var kind in new[] { "valid", "issuer", "audience", "subject", "signature",
            "kid", "algorithm", "expired", "overlong", "future_iat", "missing" })
        {
            var token = kind == "missing" ? null : Token(run, key, kind);
            await CheckAuthority(run, client, token, kind == "valid" ? 400 : 401);
        }
        Console.WriteLine("H1_EXISTING_SERVICE_MIDDLEWARE_VALIDATED_NOT_BUSINESS_ELIGIBILITY");
    }

    private static async Task Delivery(Run run)
    {
        var state = ReadState(run);
        Demand(state.MarkerPassed && IsContainerId(state.ProbeId), "H1_MARKER_REQUIRED");
        var id = state.ProbeId;
        byte[]? last = null;
        using var client = run.AuthClient();
        try
        {
            var seen = 0;
            long previousExpiry = 0;
            var until = DateTimeOffset.UtcNow.AddMinutes(12);
            while (seen < 3 && DateTimeOffset.UtcNow < until)
            {
                run.CheckTokenDirectory();
                var token = ReadToken(run);
                if (token.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(Marker)))
                { CryptographicOperations.ZeroMemory(token); await Task.Delay(250); continue; }
                var changed = last is null || !token.AsSpan().SequenceEqual(last);
                if (changed)
                {
                    var expiry = Expiry(run, token);
                    Demand(expiry > previousExpiry && (seen == 0 || expiry - previousExpiry >= 240),
                        "H1_RENEWAL_EXPIRY");
                    await Inspect(run, id, first: false);
                    using var read = await Docker(run, "container", "start", "--attach", id);
                    Demand(read.ExitCode == 0 && read.Error.Length == 0 && ValidTokenLength(read.Output.Length) &&
                        read.Output.AsSpan().SequenceEqual(token), "H1_TOKEN_CAPTURE");
                    await InspectExit(run, id, 0);
                    await CheckAuthority(run, client, SafeAscii(token), 400);
                    if (last is not null) CryptographicOperations.ZeroMemory(last);
                    last = token;
                    previousExpiry = expiry;
                    seen++;
                    Console.WriteLine("H1_DELIVERY_OBSERVATION_" + seen);
                }
                else CryptographicOperations.ZeroMemory(token);
                if (seen < 3) await Task.Delay(500);
            }
            Demand(seen == 3 && last is not null, "H1_TWO_RENEWALS_REQUIRED");
            await RequestStop(run);
            await WaitStopped(run);
            var expiryUntil = DateTimeOffset.FromUnixTimeSeconds(previousExpiry).AddSeconds(32);
            Demand(expiryUntil <= DateTimeOffset.UtcNow.AddSeconds(633), "H1_EXPIRY_BOUND");
            while (DateTimeOffset.UtcNow <= expiryUntil) await Task.Delay(250);
            await CheckAuthority(run, client, SafeAscii(last!), 401);
            CryptographicOperations.ZeroMemory(last!);
            last = null;

            await NegativeRead(run, id, [], 0);
            RemoveOwnedFile(run, run.TokenFile);
            await NegativeRead(run, id, null, 42);
            await NegativeRead(run, id, new byte[8193], 0, oversized: true);
            RemoveOwnedFile(run, run.TokenFile);
            await Inspect(run, id, first: false);
            using (var missing = await Docker(run, "container", "start", "--attach", id))
                Demand(missing.ExitCode == 42 && missing.Output.Length == 0 && missing.Error.Length == 0,
                    "H1_REMOVAL");
            await InspectExit(run, id, 42);
            await UnreadableRead(run, id);
        }
        finally
        {
            if (last is not null) CryptographicOperations.ZeroMemory(last);
            await RequestStop(run);
            await WaitStopped(run);
            await RemoveProbe(run, id);
            RemoveOwnedFile(run, run.TokenFile);
            await WriteState(run, state with { Producer = "stopped", ProbeRemoved = true });
        }
    }

    private static async Task Stop(Run run)
    {
        await RequestStop(run);
        await WaitStopped(run);
        var state = ReadState(run);
        Demand(IsContainerId(state.ProbeId), "H1_PROBE_ID");
        if (state.ProbeRemoved)
        {
            using var absent = await Docker(run, TimeSpan.FromSeconds(5), "container", "inspect",
                "--format", "{{json .}}", state.ProbeId);
            Demand(absent.ExitCode != 0 && absent.Output.Length == 0 &&
                SafeAscii(absent.Error).Contains("No such", StringComparison.OrdinalIgnoreCase),
                "H1_CLEANUP_VERIFY");
        }
        else await RemoveProbe(run, state.ProbeId);
        RemoveOwnedFile(run, run.TokenFile);
        RemoveOwnedFile(run, run.StopFile);
    }

    private static async Task NegativeRead(Run run, string id, byte[]? bytes, int exit, bool oversized = false)
    {
        if (bytes is not null) await AtomicWrite(run.TokenFile, bytes, run);
        await Inspect(run, id, first: false);
        using var read = await Docker(run, "container", "start", "--attach", id);
        Demand(read.ExitCode == exit && read.Error.Length == 0, "H1_NEGATIVE_READ");
        Demand(oversized ? read.Output.Length == 8193 && !ValidTokenLength(read.Output.Length)
            : read.Output.Length == 0, "H1_NEGATIVE_BOUND");
        await InspectExit(run, id, exit);
    }

    private static async Task UnreadableRead(Run run, string id)
    {
        Demand(OperatingSystem.IsWindows(), "H1_WINDOWS_ACL_REQUIRED");
        await AtomicWrite(run.TokenFile, Encoding.ASCII.GetBytes(Marker), run);
        run.CheckTokenDirectory();
        await UnreadableWindowsRead(run, id);
    }

    [SupportedOSPlatform("windows")]
    private static async Task UnreadableWindowsRead(Run run, string id)
    {
        run.UnderRoot(run.TokenFile); Run.NoLinks(run.TokenFile);
        var file = new FileInfo(run.TokenFile);
        var permissions = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access);
        var original = permissions.GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User;
        Demand(owner is not null, "H1_WINDOWS_OWNER");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        try
        {
            foreach (var sid in new[] { owner!, system })
                permissions.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ReadData,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            FileSystemAclExtensions.SetAccessControl(file, permissions);
            var effective = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access);
            var rules = effective.GetAccessRules(true, false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Demand(HasReadDenial(rules, owner!) && HasReadDenial(rules, system), "H1_UNREADABLE_ACL");
            // Metadata/existence is independently observed; absence is not relabelled unreadability.
            Demand(File.Exists(run.TokenFile), "H1_UNREADABLE_FILE_EXISTS");
            await Inspect(run, id, first: false);
            using var capture = await Docker(run, "container", "start", "--attach", id);
            Demand(capture.ExitCode == 42 && capture.Output.Length == 0 && capture.Error.Length == 0,
                "H1_UNREADABLE_DELIVERY");
            await InspectExit(run, id, 42);
        }
        finally
        {
            try
            {
                var restore = new FileSecurity();
                restore.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                FileSystemAclExtensions.SetAccessControl(file, restore);
                var restored = FileSystemAclExtensions.GetAccessControl(file, AccessControlSections.Access)
                    .GetSecurityDescriptorBinaryForm();
                try { Demand(restored.AsSpan().SequenceEqual(original), "H1_UNREADABLE_RESTORE"); }
                finally { CryptographicOperations.ZeroMemory(restored); }
            }
            finally { CryptographicOperations.ZeroMemory(original); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool HasReadDenial(IEnumerable<FileSystemAccessRule> rules, SecurityIdentifier sid) =>
        rules.Any(rule => rule.IdentityReference.Equals(sid) && rule.AccessControlType == AccessControlType.Deny &&
            !rule.IsInherited && (rule.FileSystemRights & FileSystemRights.ReadData) != 0);

    private static async Task CheckAuthority(Run run, HttpClient client, string? token, int expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/assignee-eligibility")
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        Demand((int)response.StatusCode == expected, "H1_SERVICE_VALIDATOR");
        Demand(response.Headers.CacheControl?.NoStore == true, "H1_SERVICE_CACHE");
        if (expected == 401)
            Demand(response.Headers.WwwAuthenticate.Any(h => h.Scheme == "Bearer"), "H1_SERVICE_CHALLENGE");
    }

    private static string Token(Run run, RSA key, string kind)
    {
        using var other = RSA.Create(2048);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var credentials = new SigningCredentials(new RsaSecurityKey(kind == "signature" ? other : key)
        { KeyId = kind == "kid" ? "zeka-b2-local-unknown" : Kid },
            kind == "algorithm" ? SecurityAlgorithms.RsaSha512 : SecurityAlgorithms.RsaSha256);
        var payload = new JwtPayload
        {
            ["iss"] = kind == "issuer" ? "https://invalid.example.invalid/" : run.ServiceIssuer,
            ["aud"] = kind == "audience" ? "zeka-api-local-b2" : run.ServiceAudience,
            ["sub"] = kind == "subject" ? "system:serviceaccount:zeka-local:wrong" : run.ServiceSubject,
            ["iat"] = kind == "expired" ? now - 661 : kind == "overlong" ? now - 1 :
                kind == "future_iat" ? now + 31 : now,
            ["nbf"] = now - 1,
            ["exp"] = kind == "expired" ? now - 61 : now + 600
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(credentials), payload));
    }

    private static long Expiry(Run run, byte[] bytes)
    {
        Demand(ValidTokenLength(bytes.Length), "H1_TOKEN_SIZE");
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(SafeAscii(bytes));
        Demand(jwt.Header.Alg == "RS256" && jwt.Header.Kid == Kid &&
            jwt.Issuer == run.ServiceIssuer && jwt.Audiences.SequenceEqual([run.ServiceAudience]) &&
            jwt.Subject == run.ServiceSubject, "H1_TOKEN_CONTRACT");
        var iat = jwt.Payload.Iat;
        var exp = jwt.Payload.Exp;
        Demand(iat is not null && exp is not null && exp - iat == 600 &&
            iat <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30, "H1_TOKEN_TIME");
        return exp!.Value;
    }

    private static async Task Inspect(Run run, string id, bool first)
    {
        using var capture = await Docker(run, "container", "inspect", "--format", "{{json .}}", id);
        Demand(capture.ExitCode == 0 && capture.Error.Length == 0, "H1_INSPECT");
        using var document = JsonDocument.Parse(capture.Output);
        var item = document.RootElement;
        Demand(item.ValueKind == JsonValueKind.Object, "H1_INSPECT_INVENTORY");
        Demand(Text(item, "Id") == id && Text(item, "Image") == run.ImageId &&
            Text(item, "Name") == "/" + run.ProbeName, "H1_INSPECT_IDENTITY");
        var config = item.GetProperty("Config");
        var host = item.GetProperty("HostConfig");
        var state = item.GetProperty("State");
        Demand(Text(state, "Status") == (first ? "created" : "exited") &&
            !state.GetProperty("Running").GetBoolean(), "H1_INSPECT_STATE");
        Demand(Text(config, "User") == run.ProbeUser && !config.GetProperty("Tty").GetBoolean() &&
            !config.GetProperty("OpenStdin").GetBoolean() && !config.GetProperty("AttachStdin").GetBoolean(),
            "H1_INSPECT_IO");
        Demand(config.GetProperty("Entrypoint").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["/bin/sh"]) &&
            config.GetProperty("Cmd").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["-c", ReadCommand]),
            "H1_INSPECT_COMMAND");
        Demand(config.GetProperty("Labels").GetProperty("zeka.b2.run").GetString() == run.RunId,
            "H1_INSPECT_OWNER");
        var environment = config.GetProperty("Env").EnumerateArray().Select(x => x.GetString()!).ToArray();
        Demand(environment.All(SafeImageEnvironment) && environment.Order(StringComparer.Ordinal)
            .SequenceEqual(run.ExpectedEnvironment.Order(StringComparer.Ordinal)), "H1_INSPECT_ENV");
        Demand(!config.TryGetProperty("Volumes", out var declared) || declared.ValueKind is JsonValueKind.Null ||
            !declared.EnumerateObject().Any(), "H1_INSPECT_DECLARED_VOLUME");
        Demand(config.GetProperty("Healthcheck").GetProperty("Test").EnumerateArray()
            .Select(x => x.GetString()).SequenceEqual(["NONE"]), "H1_INSPECT_HEALTH");
        Demand(Text(host, "NetworkMode") == "none" && host.GetProperty("ReadonlyRootfs").GetBoolean() &&
            !host.GetProperty("Privileged").GetBoolean() && Text(host.GetProperty("RestartPolicy"), "Name") == "no",
            "H1_INSPECT_ISOLATION");
        Demand(host.GetProperty("CapDrop").EnumerateArray().Select(x => x.GetString()).SequenceEqual(["ALL"]) &&
            EmptyArray(host, "CapAdd") && host.GetProperty("SecurityOpt").EnumerateArray().All(x =>
                x.GetString() is "no-new-privileges" or "no-new-privileges=true") &&
            host.GetProperty("SecurityOpt").GetArrayLength() == 1, "H1_INSPECT_CAPS");
        Demand(Text(host.GetProperty("LogConfig"), "Type") == "none" &&
            !host.GetProperty("LogConfig").GetProperty("Config").EnumerateObject().Any() &&
            Text(item, "LogPath") == "", "H1_INSPECT_LOGGING");
        foreach (var key in new[] { "PidMode", "IpcMode", "UTSMode", "UsernsMode", "CgroupnsMode" })
            Demand(!Text(host, key).Equals("host", StringComparison.OrdinalIgnoreCase), "H1_INSPECT_NAMESPACE");
        Demand(EmptyArray(host, "VolumesFrom") && EmptyArray(host, "Devices") && EmptyArray(host, "DeviceRequests") &&
            EmptyObjectOrNull(host, "PortBindings"), "H1_INSPECT_ACCESS");
        var networks = item.GetProperty("NetworkSettings").GetProperty("Networks");
        Demand(networks.EnumerateObject().All(n => n.Name == "none"), "H1_INSPECT_NETWORK");
        var mounts = item.GetProperty("Mounts").EnumerateArray().ToArray();
        Demand(mounts.Length == 1 && Text(mounts[0], "Type") == "bind" &&
            Text(mounts[0], "Destination") == "/run/zeka-local" && !mounts[0].GetProperty("RW").GetBoolean(),
            "H1_INSPECT_MOUNT");
        var actualSource = Text(mounts[0], "Source").Replace('\\', '/').TrimEnd('/');
        var intended = run.TokenDirectory.Replace('\\', '/').TrimEnd('/');
        // Docker Desktop may report a VM-translated bind path. It must be explicitly
        // admitted in the manifest; never assume an equivalent suffix is enough.
        Demand(actualSource.Equals(run.ProbeBindSource ?? intended, StringComparison.OrdinalIgnoreCase),
            "H1_INSPECT_BIND_SOURCE");
        run.CheckTokenDirectory();
    }

    private static async Task InspectExit(Run run, string id, int exit)
    {
        using var capture = await Docker(run, "container", "inspect", "--format", "{{json .}}", id);
        Demand(capture.ExitCode == 0 && capture.Error.Length == 0, "H1_EXIT_INSPECT");
        using var json = JsonDocument.Parse(capture.Output);
        var state = json.RootElement.GetProperty("State");
        Demand(!state.GetProperty("Running").GetBoolean() &&
            state.GetProperty("ExitCode").GetInt32() == exit && Text(state, "Error") == "", "H1_EXIT_STATE");
    }

    private static async Task RemoveProbe(Run run, string id)
    {
        using (var inspect = await Docker(run, "container", "inspect", "--format", "{{json .}}", id))
        {
            Demand(inspect.ExitCode == 0 && inspect.Error.Length == 0, "H1_CLEANUP_INSPECT");
            using var json = JsonDocument.Parse(inspect.Output);
            var item = json.RootElement;
            Demand(Text(item, "Id") == id && Text(item, "Name") == "/" + run.ProbeName &&
                item.GetProperty("Config").GetProperty("Labels").GetProperty("zeka.b2.run").GetString() == run.RunId,
                "H1_CLEANUP_OWNER");
            if (item.GetProperty("State").GetProperty("Running").GetBoolean())
            {
                using var stop = await Docker(run, TimeSpan.FromSeconds(5), "container", "stop", "--time", "1", id);
                if (stop.ExitCode != 0)
                {
                    using var kill = await Docker(run, TimeSpan.FromSeconds(5), "container", "kill", id);
                    Demand(kill.ExitCode == 0, "H1_CLEANUP_KILL");
                }
            }
        }
        await InspectExitStopped(run, id);
        using (var remove = await Docker(run, TimeSpan.FromSeconds(5), "container", "rm", id))
            Demand(remove.ExitCode == 0 && remove.Error.Length == 0, "H1_CLEANUP_REMOVE");
        using var absent = await Docker(run, TimeSpan.FromSeconds(5), "container", "inspect", "--format", "{{json .}}", id);
        Demand(absent.ExitCode != 0 && absent.Output.Length == 0 &&
            SafeAscii(absent.Error).Contains("No such", StringComparison.OrdinalIgnoreCase), "H1_CLEANUP_VERIFY");
    }

    private static async Task InspectExitStopped(Run run, string id)
    {
        using var capture = await Docker(run, TimeSpan.FromSeconds(5), "container", "inspect", "--format", "{{json .}}", id);
        Demand(capture.ExitCode == 0, "H1_CLEANUP_STOP_INSPECT");
        using var json = JsonDocument.Parse(capture.Output);
        Demand(!json.RootElement.GetProperty("State").GetProperty("Running").GetBoolean(),
            "H1_CLEANUP_UNVERIFIED");
    }

    private static bool SafeImageEnvironment(string? value)
    {
        if (value is null || !value.Contains('=')) return false;
        var key = value[..value.IndexOf('=')];
        return key is "PATH" or "APP_UID" or "ASPNETCORE_HTTP_PORTS" or "ASPNETCORE_URLS" or
            "DOTNET_RUNNING_IN_CONTAINER" or "DOTNET_VERSION" or "ASPNET_VERSION";
    }

    private static bool IsExplicitNoneLogsFailure(string error) =>
        error.Contains("configured logging driver does not support reading", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("logging driver does not support reading", StringComparison.OrdinalIgnoreCase);

    private static bool EmptyArray(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0;

    private static bool EmptyObjectOrNull(JsonElement element, string name) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : "";

    private static bool IsContainerId(string value) => value.Length == 64 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static Task<Capture> Docker(Run run, params string[] args) =>
        Docker(run, TimeSpan.FromSeconds(10), args);

    private static async Task<Capture> Docker(Run run, TimeSpan timeout, params string[] args)
    {
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE",
            "APPDATA", "LOCALAPPDATA", "DOCKER_CONFIG" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) start.Environment[name] = value;
        }
        start.ArgumentList.Add("--context"); start.ArgumentList.Add(run.DockerContext);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        Demand(process.Start(), "H1_CHILD_START");
        using var cancel = new CancellationTokenSource(timeout);
        var output = Bounded(process.StandardOutput.BaseStream, 8193, cancel);
        var error = Bounded(process.StandardError.BaseStream, 4096, cancel);
        try
        {
            await process.WaitForExitAsync(cancel.Token);
            return new Capture(process.ExitCode, await output, await error);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            byte[]? outBytes = null, errBytes = null;
            try { outBytes = await output; } catch { }
            try { errBytes = await error; } catch { }
            if (outBytes is not null) CryptographicOperations.ZeroMemory(outBytes);
            if (errBytes is not null) CryptographicOperations.ZeroMemory(errBytes);
            throw new H1Failure("H1_CAPTURE_FAILED");
        }
    }

    private static async Task<byte[]> Bounded(Stream source, int limit, CancellationTokenSource cancel)
    {
        using var target = new MemoryStream(limit);
        var buffer = new byte[1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancel.Token);
                if (read == 0) return target.ToArray();
                if (target.Length + read > limit) { cancel.Cancel(); throw new H1Failure("H1_CAPTURE_LIMIT"); }
                await target.WriteAsync(buffer.AsMemory(0, read), cancel.Token);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (target.TryGetBuffer(out var owned)) CryptographicOperations.ZeroMemory(owned.AsSpan());
        }
    }

    private static string SafeAscii(byte[] bytes)
    {
        Demand(bytes.All(b => b is >= 32 and <= 126 or 9 or 10 or 13), "H1_ASCII");
        return Encoding.ASCII.GetString(bytes);
    }

    private static byte[] ReadToken(Run run)
    {
        var file = new FileInfo(run.TokenFile);
        Demand(file.Exists && file.Length <= 8192 && file.Length > 0, "H1_TOKEN_SIZE");
        Run.NoLinks(run.TokenFile);
        var bytes = File.ReadAllBytes(run.TokenFile);
        if (!ValidTokenLength(bytes.Length))
        { CryptographicOperations.ZeroMemory(bytes); throw new H1Failure("H1_TOKEN_SIZE"); }
        return bytes;
    }

    private static async Task AtomicWrite(string path, byte[] bytes, Run run)
    {
        Run.NoLinks(Path.GetDirectoryName(path)!);
        var temporary = path + "." + run.RunId + ".tmp";
        Demand(!File.Exists(temporary), "H1_TEMP_COLLISION");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record State
    {
        public string ProbeId { get; init; } = "";
        public bool MarkerPassed { get; init; }
        public bool ProbeRemoved { get; init; }
        public string Producer { get; init; } = "";
    }

    private static State ReadState(Run run)
    {
        Run.NoLinks(run.StopFile);
        Demand(File.Exists(run.StopFile) && new FileInfo(run.StopFile).Length <= 1024, "H1_STATE_REQUIRED");
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(run.StopFile), Json);
        Demand(state is not null, "H1_STATE_INVALID");
        return state!;
    }

    private static async Task WriteState(Run run, State state) =>
        await AtomicWrite(run.StopFile, JsonSerializer.SerializeToUtf8Bytes(state, Json), run);

    private static bool StopRequested(Run run)
    {
        Run.NoLinks(run.StopFile);
        Demand(new FileInfo(run.StopFile).Length <= 1024, "H1_STATE_INVALID");
        return File.ReadAllText(run.StopFile).Trim() == "stop";
    }

    private static Task RequestStop(Run run)
    {
        if (!File.Exists(run.StopFile)) return Task.CompletedTask;
        Run.NoLinks(run.StopFile);
        Demand(new FileInfo(run.StopFile).Length <= 1024, "H1_STATE_INVALID");
        var text = File.ReadAllText(run.StopFile).Trim();
        if (text == "stop") return Task.CompletedTask;
        var state = JsonSerializer.Deserialize<State>(text, Json);
        if (state?.Producer is "stopped" or "not-started") return Task.CompletedTask;
        return AtomicWrite(run.StopFile, Encoding.ASCII.GetBytes("stop"), run);
    }

    private static async Task WaitStopped(Run run)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < until)
        {
            if (!File.Exists(run.StopFile)) return;
            try { if (ReadState(run).Producer is "stopped" or "not-started") return; }
            catch (H1Failure) { }
            catch (JsonException) { }
            await Task.Delay(100);
        }
        throw new H1Failure("H1_PRODUCER_CLEANUP_UNVERIFIED");
    }

    private static bool DeliveryAdmitted(Run run)
    {
        using var fresh = Run.Load(run.ManifestPath);
        Demand(fresh.RunId == run.RunId && fresh.TokenFile == run.TokenFile &&
            fresh.ImageId == run.ImageId && fresh.ServiceIssuer == run.ServiceIssuer, "H1_MANIFEST_DRIFT");
        return fresh.Root.GetProperty("service").TryGetProperty("tokenDeliveryAdmitted", out var admitted) &&
            admitted.ValueKind == JsonValueKind.True;
    }

    private static void RemoveOwnedFile(Run run, string path)
    {
        run.UnderRoot(path); Run.NoLinks(path);
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Demand(bool value, string code)
    { if (!value) throw new H1Failure(code); }

    private sealed class H1Failure(string code) : Exception(code) { }

    private sealed class Capture(int exitCode, byte[] output, byte[] error) : IDisposable
    {
        public int ExitCode { get; } = exitCode;
        public byte[] Output { get; } = output;
        public byte[] Error { get; } = error;
        public void Dispose() { CryptographicOperations.ZeroMemory(Output); CryptographicOperations.ZeroMemory(Error); }
    }

    private sealed class Run : IDisposable
    {
        private readonly JsonDocument document;
        public JsonElement Root => document.RootElement;
        public string ManifestPath { get; }
        public string RunId { get; }
        public string PrivateRoot { get; }
        public string TokenFile { get; }
        public string TokenDirectory => Path.GetDirectoryName(TokenFile)!;
        public string StopFile { get; }
        public string ImageId { get; }
        public string DockerContext { get; }
        public string ProbeUser { get; }
        public string ProbeName { get; }
        public string? ProbeBindSource { get; }
        public string[] ExpectedEnvironment { get; }
        public string ServiceIssuer { get; }
        public string ServiceAudience { get; }
        public string ServiceSubject { get; }

        private Run(string path, JsonDocument json)
        {
            ManifestPath = path; document = json;
            var root = Root;
            var candidate = Text(root, "sourceCommit");
            Demand(root.GetProperty("schemaVersion").GetInt32() == 1 &&
                Text(root, "sourceBaseline") == Source && candidate.Length == 40 &&
                candidate.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                candidate == Environment.GetEnvironmentVariable("ZEKA_B2_SOURCE_COMMIT") &&
                Text(root, "environment") == "Development" &&
                root.GetProperty("exclusiveTarget").GetBoolean(), "H1_MANIFEST_ADMISSION");
            RunId = Text(root, "runId");
            Demand(RunId.Length is >= 8 and <= 80 && RunId.All(c =>
                char.IsAsciiLetterOrDigit(c) || c == '-'), "H1_RUN_ID");
            PrivateRoot = Path.GetFullPath(Text(root, "privateRoot"));
            var admittedRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeka", "B2", RunId));
            Demand(PrivateRoot.Equals(admittedRoot, StringComparison.OrdinalIgnoreCase), "H1_PRIVATE_ROOT");
            NoLinks(PrivateRoot); UnderRoot(path);
            Demand(path.Equals(Path.Combine(PrivateRoot, "manifest.json"),
                StringComparison.OrdinalIgnoreCase), "H1_MANIFEST_LOCATION");
            var postgres = root.GetProperty("postgres");
            Demand(Text(postgres, "host") == "127.0.0.1" && postgres.GetProperty("port").GetInt32() == 5432 &&
                Text(postgres, "authDatabase") == "zekaAuthdb" &&
                Text(postgres, "containerId").Length == 64, "H1_TARGET");
            var paths = root.GetProperty("paths");
            TokenFile = OwnedPath(paths, "tokenFile");
            StopFile = OwnedPrivatePath(paths, "producerStopState");
            Demand(TokenFile.Equals(Path.Combine(PrivateRoot, "token", "service-token"),
                StringComparison.OrdinalIgnoreCase), "H1_TOKEN_PATH");
            var probe = root.GetProperty("probe");
            ImageId = Text(probe, "imageId");
            Demand(ImageId.StartsWith("sha256:", StringComparison.Ordinal) &&
                IsContainerId(ImageId[7..]), "H1_IMAGE_ID");
            DockerContext = Text(probe, "context");
            Demand(DockerContext.Length > 0 && !DockerContext.Any(char.IsControl), "H1_DOCKER_CONTEXT");
            var user = Text(probe, "user"); var group = Text(probe, "group");
            Demand(uint.TryParse(user, out var uid) && uid > 0 &&
                uint.TryParse(group, out _), "H1_NON_ROOT");
            ProbeUser = user + ":" + group;
            ProbeName = Text(probe, "containerName");
            Demand(ProbeName == "zeka-b2-" + RunId + "-mountprobe", "H1_PROBE_NAME");
            ExpectedEnvironment = probe.GetProperty("expectedEnvironment").EnumerateArray()
                .Select(x => x.GetString() ?? "").ToArray();
            Demand(ExpectedEnvironment.Length > 0 && ExpectedEnvironment.All(SafeImageEnvironment), "H1_IMAGE_ENV");
            ProbeBindSource = Text(probe, "bindSource");
            if (ProbeBindSource.Length == 0) ProbeBindSource = null;
            var service = root.GetProperty("service");
            ServiceIssuer = Text(service, "issuer");
            ServiceAudience = Text(service, "audience");
            ServiceSubject = Text(service, "subject");
            Demand(ServiceIssuer == "https://host.docker.internal:19445" &&
                ServiceAudience == "zeka-auth-assignee-local-test" &&
                ServiceSubject == "system:serviceaccount:zeka-local:clientmanagement" &&
                service.GetProperty("lifetimeSeconds").GetInt32() == 600 &&
                service.GetProperty("renewalSeconds").GetInt32() == 240, "H1_SERVICE_CONTRACT");
            var auth = root.GetProperty("auth");
            Demand(Text(auth, "baseUrl") == "https://localhost:19444" &&
                Text(auth, "origin") == "https://localhost:19443" &&
                Text(auth, "issuer") == "https://authman.api:8443" &&
                Text(auth, "audience") == "zeka-api-local-b2", "H1_AUTH_CONTRACT");
        }

        public static Run Load(string path)
        {
            Demand(Path.IsPathFullyQualified(path), "H1_MANIFEST_PATH");
            NoLinks(path);
            Demand(new FileInfo(path).Length is > 0 and <= 131072, "H1_MANIFEST_SIZE");
            return new Run(Path.GetFullPath(path), JsonDocument.Parse(File.ReadAllBytes(path)));
        }

        public string OwnedPath(JsonElement paths, string name)
        {
            var path = Text(paths, name); Demand(Path.IsPathFullyQualified(path), "H1_PRIVATE_PATH");
            path = Path.GetFullPath(path); UnderRoot(path); NoLinks(path); return path;
        }

        private string OwnedPrivatePath(JsonElement paths, string name)
        {
            var path = OwnedPath(paths, name);
            Demand(path.StartsWith(Path.Combine(PrivateRoot, "private") + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase), "H1_SECRET_PATH");
            return path;
        }

        public void UnderRoot(string path) =>
            Demand(Path.GetFullPath(path).StartsWith(PrivateRoot.TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "H1_PATH_ESCAPE");

        public static void NoLinks(string path)
        {
            var current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                    Demand((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0, "H1_REPARSE");
                current = Path.GetDirectoryName(current);
            }
        }

        public void CheckTokenDirectory()
        {
            NoLinks(TokenDirectory);
            Demand(Directory.Exists(TokenDirectory) && !Directory.EnumerateDirectories(TokenDirectory).Any(),
                "H1_TOKEN_DIRECTORY");
            foreach (var entry in Directory.EnumerateFiles(TokenDirectory))
            {
                NoLinks(entry);
                Demand(entry.Equals(TokenFile, StringComparison.OrdinalIgnoreCase) ||
                    entry.Equals(TokenFile + "." + RunId + ".tmp", StringComparison.OrdinalIgnoreCase),
                    "H1_TOKEN_DIRECTORY_CONTENT");
            }
        }

        public RSA SigningKey()
        {
            var path = OwnedPrivatePath(Root.GetProperty("paths"), "producerSigningPem");
            Demand(new FileInfo(path).Length is > 0 and <= 32768, "H1_SIGNING_KEY");
            var rsa = RSA.Create(); rsa.ImportFromPem(File.ReadAllText(path));
            Demand(rsa.KeySize >= 2048, "H1_SIGNING_KEY"); return rsa;
        }

        public X509Certificate2 ProducerCertificate()
        {
            var path = OwnedPrivatePath(Root.GetProperty("paths"), "producerPfx");
            Demand(new FileInfo(path).Length is > 0 and <= 65536, "H1_PRODUCER_CERTIFICATE");
            return new X509Certificate2(path, (string?)null, X509KeyStorageFlags.EphemeralKeySet);
        }

        public X509Certificate2 RootCertificate() =>
            X509Certificate2.CreateFromPemFile(OwnedPath(Root.GetProperty("paths"), "caPem"));

        public HttpClient AuthClient()
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                if (request.RequestUri?.GetLeftPart(UriPartial.Authority) != "https://localhost:19444" ||
                    certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch |
                    SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var ca = RootCertificate();
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(ca);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            };
            return new HttpClient(handler) { BaseAddress = new Uri("https://localhost:19444"),
                Timeout = TimeSpan.FromSeconds(10) };
        }

        public void Dispose() => document.Dispose();
    }
}
