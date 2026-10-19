using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ocelot.Configuration.Repository;
using Ocelot.DependencyInjection;
using Ocelot.Middleware;
using Ocelot.Requester;
using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Workflow interface (Hephaestus owns the workflow):
/// Filter exactly TestIdentity, require one discovered/executed test, zero skips and exit 0.
/// Set ZEKA_TRUST_PROBE_EVIDENCE to a new directory immediately below RUNNER_TEMP.
/// Remove restore credentials from the testhost environment before launch.
/// Windows requires observed github-hosted/win22/runneradmin identity; Linux runs
/// in the unchanged protected job. Children inherit an explicit non-secret allowlist.
///
/// Evidence: receipts.jsonl, ownership.json, public-root.cer, child-*/result.json and
/// child-*/result.trx (all public/sanitized). No private key is ever serialized.
/// Fallback: only on the admitted Windows job, read ownership.json, verify its SID,
/// subject, thumbprint and public-root.cer bytes against the exact CurrentUser root,
/// remove that root alone, verify absence, append a separate fallback receipt.
/// Never turn a failed test into success. The fallback is not proof of primary cleanup.
/// </summary>
internal static class GatewayHandlerTrustProbeSupport
{
    internal const string ChildMode = "ZEKA_TRUST_PROBE_CHILD";
    internal const string TestIdentity = "Infrastructure.IntegrationTests.GatewayHandlerTrustProbeTests.Real_pool_default_validation_fixed_package";
    private const string EvidenceVariable = "ZEKA_TRUST_PROBE_EVIDENCE";
    private const string ChildDirectory = "ZEKA_TRUST_PROBE_CHILD_DIRECTORY";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    internal static async Task RunSupervisorAsync(ITestOutputHelper output)
    {
        // This check precedes key generation, directory creation and every store access.
        var identity = AdmitEnvironment();
        var parentTrustFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        var parentTrustDirectory = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
        var directory = EvidenceDirectory();
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var receipts = Path.Combine(directory, "receipts.jsonl");
        void Receipt(object value)
        {
            var line = JsonSerializer.Serialize(value, Json);
            File.AppendAllText(receipts, line + Environment.NewLine);
            output.WriteLine(line);
        }
        Receipt(new { phase = "admission", identity, os = RuntimeInformation.OSDescription,
            runtime = Environment.Version.ToString(), ocelot = OcelotVersion(), test = TestIdentity,
            source = SourceIdentity(), childEnvironment = "explicit-allowlist-no-restore-credentials" });

        using var rootKey = RSA.Create(2048);
        var id = Guid.NewGuid().ToString("N");
        using var root = CreateRoot(rootKey, "Zeka-Ocelot-Trust-Probe-" + id);
        using var otherKey = RSA.Create(2048);
        using var otherRoot = CreateRoot(otherKey, "Zeka-Ocelot-Untrusted-Control-" + id);
        using var leafKey = RSA.Create(2048);
        using var trustedLeaf = CreateLeaf(leafKey, root);
        using var untrustedLeaf = CreateLeaf(leafKey, otherRoot);
        Check(trustedLeaf.NotBefore == untrustedLeaf.NotBefore && trustedLeaf.NotAfter == untrustedLeaf.NotAfter, "matched-validity");
        // Only public certificate bytes cross the process/evidence boundary.
        File.WriteAllBytes(Path.Combine(directory, "public-root.cer"), root.RawData);
        File.WriteAllBytes(Path.Combine(directory, "untrusted-root.cer"), otherRoot.RawData);
        File.WriteAllBytes(Path.Combine(directory, "trusted-leaf.cer"), trustedLeaf.RawData);
        File.WriteAllBytes(Path.Combine(directory, "untrusted-leaf.cer"), untrustedLeaf.RawData);
        Write(Path.Combine(directory, "ownership.json"), new { schema = 1, probeId = id,
            accountSid = identity.AccountSid, rootThumbprint = root.Thumbprint, rootSubject = root.Subject,
            publicRootSha256 = Convert.ToHexString(SHA256.HashData(root.RawData)), store = "CurrentUser/Root",
            windows = OperatingSystem.IsWindows(), state = "prepared" });

        var observations = new ConcurrentDictionary<string, int>();
        WebApplication? trustedServer = null;
        WebApplication? untrustedServer = null;
        var caFile = Path.Combine(directory, "scoped-ca.pem");
        var caDirectory = Path.Combine(directory, "scoped-ca-directory");
        Exception? failure = null;
        try
        {
            trustedServer = await StartEndpointAsync(trustedLeaf, observations);
            untrustedServer = await StartEndpointAsync(untrustedLeaf, observations);
            var trustedUri = Address(trustedServer);
            var untrustedUri = Address(untrustedServer);
            foreach (var lifecycle in new[] { "normal", "startup-failure", "abnormal-child" })
            {
                await ControlAsync("before-" + lifecycle, trustedUri, false, false);
                var plannedStartupFailure = false;
                try
                {
                    if (OperatingSystem.IsWindows()) ImportOwnedRoot(root);
                    else
                    {
                        Directory.CreateDirectory(caDirectory);
                        File.WriteAllText(caFile, root.ExportCertificatePem());
                    }
                    Receipt(new { phase = "trust-acquired", lifecycle, scope = OperatingSystem.IsWindows() ? "hosted-job-account" : "dedicated-child-startup" });
                    if (lifecycle == "startup-failure") throw new PlannedStartupFailure();
                    await ControlAsync("trusted-" + lifecycle, trustedUri, true, true);
                    await ControlAsync("untrusted-" + lifecycle, untrustedUri, false, true);
                    if (lifecycle == "abnormal-child")
                        await ControlAsync("terminated-child", trustedUri, true, true, killAfterReceipt: true);
                }
                catch (PlannedStartupFailure) { plannedStartupFailure = true; }
                finally
                {
                    // The supervisor owns trust; killed children never own its removal.
                    CleanupTrust(root, caFile, caDirectory);
                    Receipt(new { phase = "primary-cleanup", lifecycle, rootAbsent = !OperatingSystem.IsWindows() || !RootPresent(root),
                        caFileAbsent = !File.Exists(caFile), caDirectoryAbsent = !Directory.Exists(caDirectory),
                        plannedStartupFailure });
                }
                Check(plannedStartupFailure == (lifecycle == "startup-failure"), "startup-failure-observed");
                await ControlAsync("after-" + lifecycle, trustedUri, false, false);
                Receipt(new { phase = "lifecycle-complete", lifecycle, freshDefaultTrustRejected = true });
            }

            async Task ControlAsync(string name, Uri target, bool success, bool scoped, bool killAfterReceipt = false)
            {
                // A live TCP listener plus certificate validity/name checks and the
                // certificate-specific child result exclude refused connection/timeout.
                using (var socket = new TcpClient())
                    await socket.ConnectAsync(IPAddress.Loopback, target.Port).WaitAsync(TimeSpan.FromSeconds(10));
                var correlation = Guid.NewGuid().ToString("N");
                var result = await RunProcessAsync(directory, name, target, correlation,
                    scoped && !OperatingSystem.IsWindows() ? caFile : null,
                    scoped && !OperatingSystem.IsWindows() ? caDirectory : null, killAfterReceipt);
                Check(result.Accepted == success, "unexpected-TLS-outcome");
                Check(result.NameValid && result.TimeValid && result.RealPool, "control-configuration");
                Check(result.ChainValid == success, "diagnostic-chain-outcome");
                if (!success)
                    Check(result.CertificateFailure && result.HttpError == "SecureConnectionError"
                        && result.AuthenticationException && result.ChainStatuses.Contains("UntrustedRoot"), "certificate-specific-rejection");
                var observed = observations.GetValueOrDefault(correlation);
                Check(observed == (success ? 1 : 0), "correlated-downstream-count");
                Receipt(new { phase = "TLS-control", name, correlation, result,
                    downstreamHttpReceipts = observed, listenerReachable = true, abnormalChildKilled = killAfterReceipt });
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            // Independent cleanup attempts; preserve the original failure, while
            // recording cleanup failures as failures instead of masking them.
            var cleanupFailures = new List<string>();
            try { CleanupTrust(root, caFile, caDirectory); } catch { cleanupFailures.Add("trust-cleanup"); }
            foreach (var server in new[] { trustedServer, untrustedServer })
                if (server is not null)
                    try
                    {
                        var port = Address(server).Port;
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await server.StopAsync(timeout.Token); await server.DisposeAsync();
                        using var check = new TcpClient();
                        try { await check.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2)); cleanupFailures.Add("listener-still-open"); }
                        catch (SocketException) { }
                    }
                    catch { cleanupFailures.Add("endpoint-cleanup"); }
            Check(parentTrustFile == Environment.GetEnvironmentVariable("SSL_CERT_FILE")
                && parentTrustDirectory == Environment.GetEnvironmentVariable("SSL_CERT_DIR"), "parent-environment-unchanged");
            Receipt(new { phase = "supervisor-cleanup", cleanupFailures, parentEnvironmentUnchanged = true,
                originalFailurePreserved = failure is not null });
            if (failure is null) Check(cleanupFailures.Count == 0, "cleanup-failure");
        }
        Receipt(new { phase = "fixed-package-complete", lifecycles = 3, requiredControlsSkipped = 0 });
    }

    private static async Task<WebApplication> StartEndpointAsync(X509Certificate2 certificate, ConcurrentDictionary<string, int> observations)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        try
        {
            app.Run(context =>
            {
                var correlation = context.Request.Headers["X-Zeka-Probe"].ToString();
                Check(Guid.TryParseExact(correlation, "N", out _), "correlation-format");
                Check(context.Request.Method == "GET" && context.Request.Path == "/probe", "endpoint-request");
                observations.AddOrUpdate(correlation, 1, (_, count) => count + 1);
                context.Response.StatusCode = 204; context.Response.Headers["X-Zeka-Probe"] = correlation;
                return Task.CompletedTask;
            });
            await app.StartAsync(); return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }

    internal static async Task RunChildAsync()
    {
        // No keys or store writes in children. The parent passes only public certs.
        var directory = Environment.GetEnvironmentVariable(ChildDirectory)!;
        var input = Read<ChildInput>(Path.Combine(directory, "input.json"));
        var target = new Uri(input.Target);
        Check(target.Scheme == "https" && target.Host == "127.0.0.1" && target.AbsolutePath == "/probe", "local-HTTPS-only");
        using var leaf = new X509Certificate2(File.ReadAllBytes(input.LeafFile));
        using var issuer = new X509Certificate2(File.ReadAllBytes(input.IssuerFile));
        var nameValid = leaf.MatchesHostname("127.0.0.1", allowWildcards: false, allowCommonName: false);
        var timeValid = DateTime.UtcNow > leaf.NotBefore.ToUniversalTime() && DateTime.UtcNow < leaf.NotAfter.ToUniversalTime();
        using var chain = new X509Chain();
        // Independent diagnostic only; never assigned to Ocelot/SslOptions. ExtraStore
        // supplies an issuer, NOT a trust anchor. No AIA/CDP is present in these certs.
        chain.ChainPolicy.ExtraStore.Add(issuer);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var chainValid = chain.Build(leaf);
        var statuses = chain.ChainStatus.Select(status => status.Status.ToString()).OrderBy(x => x).ToArray();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Routes:0:Key"] = "ZekaTrustProbe", ["Routes:0:UpstreamPathTemplate"] = "/probe",
            ["Routes:0:UpstreamHttpMethod:0"] = "GET", ["Routes:0:DownstreamPathTemplate"] = "/probe",
            ["Routes:0:DownstreamScheme"] = "https", ["Routes:0:DownstreamHostAndPorts:0:Host"] = "127.0.0.1",
            ["Routes:0:DownstreamHostAndPorts:0:Port"] = target.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Routes:0:DangerousAcceptAnyServerCertificateValidator"] = "false",
            ["Routes:0:HttpHandlerOptions:AllowAutoRedirect"] = "false",
            ["Routes:0:HttpHandlerOptions:UseCookieContainer"] = "false",
            ["Routes:0:HttpHandlerOptions:UseProxy"] = "false"
        });
        builder.Services.AddOcelot(builder.Configuration);
        await using var app = builder.Build();
        await app.UseOcelot();
        var pool = app.Services.GetRequiredService<IMessageInvokerPool>();
        Check(pool.GetType() == typeof(MessageInvokerPool), "real-pool-registration");
        var configuration = app.Services.GetRequiredService<IInternalConfigurationRepository>().Get();
        Check(!configuration.IsError && configuration.Data.Routes.Count == 1, "normal-route-creation");
        var route = configuration.Data.Routes.Single().DownstreamRoute.Single();
        Check(!route.DangerousAcceptAnyServerCertificateValidator && route.DownstreamScheme == "https", "default-validation-required");
        // Invoke the actual Ocelot-created route handler directly so its TLS exception
        // can be classified without changing it or relying on a generic gateway 502.
        // This is handler-trust proof, not a replacement browser ingress test.
        using var invoker = pool.Get(route);
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Add("X-Zeka-Probe", input.Correlation);
        ChildResult result;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await invoker.SendAsync(request, deadline.Token);
            Check((int)response.StatusCode == 204 && response.Headers.GetValues("X-Zeka-Probe").Single() == input.Correlation, "downstream-response");
            result = new(true, false, "None", false, nameValid, timeValid, chainValid, statuses, true);
        }
        catch (HttpRequestException error)
        {
            var authentication = false;
            for (Exception? inner = error; inner is not null; inner = inner.InnerException)
                authentication |= inner is AuthenticationException;
            result = new(false, error.HttpRequestError == HttpRequestError.SecureConnectionError && authentication,
                error.HttpRequestError.ToString(), authentication, nameValid, timeValid, chainValid, statuses, true);
        }
        Write(Path.Combine(directory, "result.json"), result);
        if (input.PauseAfterReceipt) await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task<ChildResult> RunProcessAsync(string owner, string name, Uri target, string correlation,
        string? caFile, string? caDirectory, bool killAfterReceipt)
    {
        var directory = Path.Combine(owner, "child-" + name); Directory.CreateDirectory(directory);
        var untrusted = name.StartsWith("untrusted-", StringComparison.Ordinal);
        Write(Path.Combine(directory, "input.json"), new ChildInput(target.AbsoluteUri, correlation,
            Path.Combine(owner, untrusted ? "untrusted-leaf.cer" : "trusted-leaf.cer"),
            Path.Combine(owner, untrusted ? "untrusted-root.cer" : "public-root.cer"), killAfterReceipt));
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = directory };
        // Exact child test only. No restore/build and no untracked executable script.
        foreach (var argument in new[] { "vstest", typeof(GatewayHandlerTrustProbeTests).Assembly.Location,
            "/TestCaseFilter:FullyQualifiedName=" + TestIdentity, "/Logger:trx;LogFileName=result.trx", "/ResultsDirectory:" + directory })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet)!;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment[ChildMode] = "handler"; start.Environment[ChildDirectory] = directory;
        if (caFile is not null) { start.Environment["SSL_CERT_FILE"] = caFile; start.Environment["SSL_CERT_DIR"] = caDirectory!; }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("child-start");
        // Drain but never publish framework stdout/stderr. Its sanitized TRX has only
        // the generic outer failure; result.json is the allowlisted native observation.
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            if (killAfterReceipt)
            {
                while (!File.Exists(Path.Combine(directory, "result.json")))
                {
                    Check(!process.HasExited, "abnormal-control-early-exit");
                    await Task.Delay(50, timeout.Token);
                }
                process.Kill(entireProcessTree: true);
            }
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout, stderr);
            if (!killAfterReceipt)
            {
                Check(process.ExitCode == 0, "child-nonzero-exit");
                var trx = XDocument.Load(Path.Combine(directory, "result.trx"));
                var counters = trx.Descendants().Single(node => node.Name.LocalName == "Counters");
                Check((string?)counters.Attribute("total") == "1" && (string?)counters.Attribute("passed") == "1"
                    && (string?)counters.Attribute("executed") == "1" && (string?)counters.Attribute("notExecuted") == "0", "child-exact-test-accounting");
            }
            else Check(process.ExitCode != 0, "abnormal-child-termination-exit");
            Write(Path.Combine(directory, "process.json"), new { exitCode = process.ExitCode, killedBySupervisor = killAfterReceipt,
                expectedExecutedTests = 1, testIdentity = TestIdentity, noCredentialsInherited = true });
            return Read<ChildResult>(Path.Combine(directory, "result.json"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }

    private static X509Certificate2 CreateRoot(RSA key, string commonName)
    {
        var request = new CertificateRequest("CN=" + commonName, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var day = DateTimeOffset.UtcNow.Date;
        return request.CreateSelfSigned(day.AddDays(-1), day.AddDays(2));
    }

    private static X509Certificate2 CreateLeaf(RSA key, X509Certificate2 issuer)
    {
        var request = new CertificateRequest("CN=Zeka-Probe-Loopback", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        var names = new SubjectAlternativeNameBuilder(); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build());
        var day = DateTimeOffset.UtcNow.Date;
        using var publicLeaf = request.Create(issuer, day.AddHours(-1), day.AddDays(1), RandomNumberGenerator.GetBytes(16));
        return publicLeaf.CopyWithPrivateKey(key);
    }

    private static void ImportOwnedRoot(X509Certificate2 root)
    {
        Check(OperatingSystem.IsWindows(), "windows-store-only"); AdmitEnvironment();
        Check(!RootPresent(root), "unique-root-before-import");
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite); using var publicRoot = new X509Certificate2(root.RawData); store.Add(publicRoot);
        Check(RootPresent(root), "root-import-observed");
    }

    private static bool RootPresent(X509Certificate2 root)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false);
        try { return matches.Cast<X509Certificate2>().Any(cert => cert.RawData.AsSpan().SequenceEqual(root.RawData)); }
        finally { foreach (var certificate in matches) certificate.Dispose(); }
    }

    private static void CleanupTrust(X509Certificate2 root, string caFile, string caDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            AdmitEnvironment();
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadWrite);
            var matches = store.Certificates.Find(X509FindType.FindByThumbprint, root.Thumbprint, false);
            foreach (var certificate in matches)
                using (certificate)
                    if (certificate.RawData.AsSpan().SequenceEqual(root.RawData)) store.Remove(certificate);
            Check(!RootPresent(root), "owned-root-removed");
        }
        // Exact probe-created paths only; never clear unrelated directories/pools.
        if (File.Exists(caFile)) File.Delete(caFile);
        if (Directory.Exists(caDirectory)) Directory.Delete(caDirectory, recursive: false);
    }

    private static EnvironmentIdentity AdmitEnvironment()
    {
        Check(Environment.Version.Major == 8 && Environment.Version.Minor == 0, "net8-runtime");
        Check(OcelotVersion().StartsWith("23.3.3", StringComparison.Ordinal), "pinned-ocelot");
        if (!OperatingSystem.IsWindows())
        {
            Check(OperatingSystem.IsLinux(), "supported-platform");
            return new("linux-child-startup-trust-only", null, Environment.GetEnvironmentVariable("ImageOS"), Environment.GetEnvironmentVariable("ImageVersion"));
        }
        Check(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true"
            && Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted"
            && Environment.GetEnvironmentVariable("ImageOS") == "win22"
            && Environment.OSVersion.Version.Build == 20348
            && Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") == "diliwo/zeka-web-api", "hosted-windows-2022-required");
        using var identity = WindowsIdentity.GetCurrent();
        var name = identity.Name.Split('\\').Last();
        Check(name.Equals("runneradmin", StringComparison.OrdinalIgnoreCase) && identity.User is not null, "dedicated-hosted-account");
        return new("github-hosted-windows-2022-per-job-VM", identity.User!.Value,
            Environment.GetEnvironmentVariable("ImageOS"), Environment.GetEnvironmentVariable("ImageVersion"));
    }

    private static string EvidenceDirectory()
    {
        var temporary = Path.GetFullPath(Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath());
        var provided = Environment.GetEnvironmentVariable(EvidenceVariable);
        var path = Path.GetFullPath(provided ?? Path.Combine(temporary, "zeka-trust-probe-" + Guid.NewGuid().ToString("N")));
        Check(Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar)) == temporary.TrimEnd(Path.DirectorySeparatorChar)
            && Path.GetFileName(path).StartsWith("zeka-trust-probe-", StringComparison.Ordinal)
            && !Directory.Exists(path), "new-owned-evidence-directory");
        return path;
    }

    private static object SourceIdentity()
    {
        // Git emits only immutable identities, never status, remotes, environment or credentials.
        string Git(string argument)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var value = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(); Check(process.ExitCode == 0 && value.Length == 40 && value.All(Uri.IsHexDigit), "source-identity"); return value;
        }
        return new { checkedOutCommit = Git("HEAD"), checkedOutTree = Git("HEAD^{tree}"),
            testAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GatewayHandlerTrustProbeTests).Assembly.Location))),
            workflowHead = Environment.GetEnvironmentVariable("GITHUB_SHA") };
    }

    private static string OcelotVersion() => typeof(MessageInvokerPool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, overwrite: true);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    private sealed class PlannedStartupFailure : Exception;
    private sealed record EnvironmentIdentity(string Boundary, string? AccountSid, string? ImageOS, string? ImageVersion);
    private sealed record ChildInput(string Target, string Correlation, string LeafFile, string IssuerFile, bool PauseAfterReceipt);
    private sealed record ChildResult(bool Accepted, bool CertificateFailure, string HttpError, bool AuthenticationException,
        bool NameValid, bool TimeValid, bool ChainValid, string[] ChainStatuses, bool RealPool);
}
