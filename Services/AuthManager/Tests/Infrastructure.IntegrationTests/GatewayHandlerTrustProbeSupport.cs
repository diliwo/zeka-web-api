using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
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
        var diagnostic = new ProbeDiagnostic("supervisor",
            value => AppendDiagnostic(diagnosticDirectory: ProbeDiagnostic.Current?.Directory, value),
            value => output.WriteLine(JsonSerializer.Serialize(value, Json)));
        using var scope = diagnostic.Activate();
        try { await RunSupervisorCoreAsync(output, diagnostic); }
        catch (Exception error) { diagnostic.Capture(error); throw; }
        finally { diagnostic.Emit(); }
    }

    private static async Task RunSupervisorCoreAsync(ITestOutputHelper output, ProbeDiagnostic diagnostic)
    {
        // This check precedes key generation, directory creation and every store access.
        diagnostic.At(ProbeOperation.EnvironmentAdmission);
        var identity = AdmitEnvironment();
        var parentTrustFile = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
        var parentTrustDirectory = Environment.GetEnvironmentVariable("SSL_CERT_DIR");
        var directory = EvidenceDirectory();
        diagnostic.Directory = directory;
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var receipts = Path.Combine(directory, "receipts.jsonl");
        var failures = new ProbeFailures();
        void Receipt(object value, bool completion = false)
        {
            string line;
            try { line = JsonSerializer.Serialize(value, Json); }
            catch (Exception error)
            {
                failures.EvidenceFailures.Add(new("receipt-serialization", ExceptionDispatchInfo.Capture(error)));
                throw;
            }
            // The durable completion marker has no subsequent fallible console write.
            try { if (!completion) output.WriteLine(line); }
            catch (Exception error)
            {
                failures.EvidenceFailures.Add(new("receipt-output", ExceptionDispatchInfo.Capture(error)));
                throw;
            }
            try { File.AppendAllText(receipts, line + Environment.NewLine); }
            catch (Exception error)
            {
                failures.EvidenceFailures.Add(new("receipt-persistence", ExceptionDispatchInfo.Capture(error)));
                throw;
            }
        }
        diagnostic.At(ProbeOperation.SourceIdentity);
        var source = SourceIdentity();
        diagnostic.Source = source;
        Receipt(new { phase = "admission", identity, os = RuntimeInformation.OSDescription,
            runtime = Environment.Version.ToString(), ocelot = OcelotVersion(), test = TestIdentity,
            source, childEnvironment = "explicit-allowlist-no-restore-credentials" });

        diagnostic.At(ProbeOperation.CertificateSetup);
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
        int? trustedPort = null, untrustedPort = null;
        await RunWithCleanupAsync(failures, async () =>
        {
            diagnostic.At(ProbeOperation.TrustedEndpointStart);
            trustedServer = await StartEndpointAsync(trustedLeaf, observations, failures);
            diagnostic.At(ProbeOperation.UntrustedEndpointStart);
            untrustedServer = await StartEndpointAsync(untrustedLeaf, observations, failures);
            diagnostic.At(ProbeOperation.TrustedAddress);
            var trustedUri = Address(trustedServer);
            diagnostic.At(ProbeOperation.UntrustedAddress);
            var untrustedUri = Address(untrustedServer);
            foreach (var lifecycle in new[] { "normal", "startup-failure", "abnormal-child" })
            {
                await ControlAsync("before-" + lifecycle, trustedUri, false, false);
                var plannedStartupFailure = false;
                await RunWithCleanupAsync(failures, async () =>
                {
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
                }, TrustCleanupSteps(root, caFile, caDirectory), TrustObservations(root, caFile, caDirectory), observed =>
                {
                    // The supervisor owns trust; killed children never own its removal.
                    Receipt(new { phase = "primary-cleanup", lifecycle, rootAbsent = observed["root-absent"],
                        caFileAbsent = observed["ca-file-absent"], caDirectoryAbsent = observed["ca-directory-absent"],
                        plannedStartupFailure, cleanupFailures = failures.CleanupStages,
                        postconditionFailures = failures.PostconditionStages, evidenceFailures = failures.EvidenceStages });
                });
                Check(plannedStartupFailure == (lifecycle == "startup-failure"), "startup-failure-observed");
                await ControlAsync("after-" + lifecycle, trustedUri, false, false);
                Receipt(new { phase = "lifecycle-complete", lifecycle, freshDefaultTrustRejected = true });
            }

            async Task ControlAsync(string name, Uri target, bool success, bool scoped, bool killAfterReceipt = false)
            {
                diagnostic.At(ProbeOperation.ControlSocket, name);
                // A live TCP listener plus certificate validity/name checks and the
                // certificate-specific child result exclude refused connection/timeout.
                using (var socket = new TcpClient())
                    await socket.ConnectAsync(IPAddress.Loopback, target.Port).WaitAsync(TimeSpan.FromSeconds(10));
                var correlation = Guid.NewGuid().ToString("N");
                var result = await RunProcessAsync(directory, name, target, correlation,
                    scoped && !OperatingSystem.IsWindows() ? caFile : null,
                    scoped && !OperatingSystem.IsWindows() ? caDirectory : null, killAfterReceipt, failures);
                diagnostic.At(ProbeOperation.ControlAssertions, name);
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
        }, [.. TrustCleanupSteps(root, caFile, caDirectory),
            .. EndpointCleanupSteps(() => trustedServer, port => trustedPort = port, "trusted-endpoint"),
            .. EndpointCleanupSteps(() => untrustedServer, port => untrustedPort = port, "untrusted-endpoint")],
        [.. TrustObservations(root, caFile, caDirectory),
            new("parent-environment-unchanged", () => Task.FromResult(
                parentTrustFile == Environment.GetEnvironmentVariable("SSL_CERT_FILE")
                && parentTrustDirectory == Environment.GetEnvironmentVariable("SSL_CERT_DIR"))),
            new("trusted-listener-closed", () => ListenerClosedAsync(trustedServer, trustedPort)),
            new("untrusted-listener-closed", () => ListenerClosedAsync(untrustedServer, untrustedPort))], observed =>
        {
            Receipt(new { phase = "supervisor-cleanup", cleanupFailures = failures.CleanupStages,
                postconditionFailures = failures.PostconditionStages, evidenceFailures = failures.EvidenceStages,
                parentEnvironmentUnchanged = observed["parent-environment-unchanged"],
                rootAbsent = observed["root-absent"], caFileAbsent = observed["ca-file-absent"],
                caDirectoryAbsent = observed["ca-directory-absent"],
                originalFailurePreserved = failures.PrimaryFailure is not null });
        }, () => Receipt(new { phase = "fixed-package-complete", lifecycles = 3, requiredControlsSkipped = 0 }, completion: true));
    }

    // Shared by the real probe and deterministic, certificate-free regressions.
    // Exceptions stay internal; receipts select only fixed stages and nullable observations.
    internal sealed record ProbeStep(string Stage, Func<Task> Run);
    internal sealed record ProbeObservation(string Stage, Func<Task<bool>> Observe);
    internal sealed record ProbeFailure(string Stage, ExceptionDispatchInfo Error);
    internal sealed class ProbeFailures
    {
        internal ExceptionDispatchInfo? PrimaryFailure { get; private set; }
        internal List<ProbeFailure> CleanupFailures { get; } = [];
        internal List<ProbeFailure> PostconditionFailures { get; } = [];
        internal List<ProbeFailure> EvidenceFailures { get; } = [];
        internal string[] CleanupStages => CleanupFailures.Select(x => x.Stage).ToArray();
        internal string[] PostconditionStages => PostconditionFailures.Select(x => x.Stage).ToArray();
        internal string[] EvidenceStages => EvidenceFailures.Select(x => x.Stage).ToArray();
        internal bool CompletionEmitted { get; set; }
        internal bool Failed => PrimaryFailure is not null || CleanupFailures.Count != 0
            || PostconditionFailures.Count != 0 || EvidenceFailures.Count != 0;

        internal void CapturePrimary(Exception error)
        {
            // A nested scope rethrows an already classified failure; do not relabel it.
            if (PrimaryFailure is null && !CleanupFailures.Concat(PostconditionFailures).Concat(EvidenceFailures)
                .Any(x => ReferenceEquals(x.Error.SourceException, error)))
                PrimaryFailure = ExceptionDispatchInfo.Capture(error);
        }

        internal void ThrowIfFailed()
        {
            var first = PrimaryFailure ?? CleanupFailures.Concat(PostconditionFailures).Concat(EvidenceFailures)
                .Select(x => x.Error).FirstOrDefault();
            first?.Throw();
        }
    }

    internal static async Task RunWithCleanupAsync(ProbeFailures failures, Func<Task> body,
        IReadOnlyList<ProbeStep> cleanup, IReadOnlyList<ProbeObservation> postconditions,
        Action<IReadOnlyDictionary<string, bool?>> receipt, Action? complete = null)
    {
        try { await body(); }
        catch (Exception error) { ProbeDiagnostic.Current?.Capture(error); failures.CapturePrimary(error); }
        foreach (var step in cleanup)
        {
            try { await step.Run(); ProbeDiagnostic.Current?.Cleanup(step.Stage, true, null); }
            catch (Exception error)
            {
                ProbeDiagnostic.Current?.Cleanup(step.Stage, false, error);
                failures.CleanupFailures.Add(new(step.Stage, ExceptionDispatchInfo.Capture(error)));
            }
        }
        var observed = new Dictionary<string, bool?>();
        foreach (var check in postconditions)
        {
            observed[check.Stage] = null; // Unknown until the observation actually returns.
            try
            {
                observed[check.Stage] = await check.Observe();
                if (observed[check.Stage] == false)
                    failures.PostconditionFailures.Add(new(check.Stage,
                        ExceptionDispatchInfo.Capture(new InvalidOperationException("probe-postcondition-failed"))));
            }
            catch (Exception error) { failures.PostconditionFailures.Add(new(check.Stage, ExceptionDispatchInfo.Capture(error))); }
        }
        ProbeDiagnostic.Current?.ObserveCleanup(observed);
        try { receipt(observed); }
        catch (Exception error) { failures.EvidenceFailures.Add(new("cleanup-receipt", ExceptionDispatchInfo.Capture(error))); }
        if (!failures.Failed && complete is not null)
        {
            try { complete(); failures.CompletionEmitted = true; }
            catch (Exception error) { failures.EvidenceFailures.Add(new("completion-receipt", ExceptionDispatchInfo.Capture(error))); }
        }
        // Receipt failure is a failed outcome and evidence limitation, never a made-up receipt.
        failures.ThrowIfFailed();
    }

    private static ProbeStep[] TrustCleanupSteps(X509Certificate2 root, string caFile, string caDirectory) =>
    [
        new("owned-root-cleanup", () => { CleanupRoot(root); return Task.CompletedTask; }),
        new("ca-file-cleanup", () => { if (!PathAbsent(caFile)) File.Delete(caFile); return Task.CompletedTask; }),
        new("ca-directory-cleanup", () => { if (!PathAbsent(caDirectory)) Directory.Delete(caDirectory, recursive: false); return Task.CompletedTask; })
    ];

    private static ProbeObservation[] TrustObservations(X509Certificate2 root, string caFile, string caDirectory) =>
    [
        new("root-absent", () => Task.FromResult(!OperatingSystem.IsWindows() || !RootPresent(root))),
        new("ca-file-absent", () => Task.FromResult(PathAbsent(caFile))),
        new("ca-directory-absent", () => Task.FromResult(PathAbsent(caDirectory)))
    ];

    private static bool PathAbsent(string path)
    {
        // Exists() can hide access/IO errors as false; those must remain unverified.
        try { _ = File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
    }

    private static ProbeStep[] EndpointCleanupSteps(Func<WebApplication?> endpoint, Action<int> port, string stage) =>
    [
        new(stage + "-address", () => { if (endpoint() is { } app) port(Address(app).Port); return Task.CompletedTask; }),
        new(stage + "-stop", async () =>
        {
            if (endpoint() is not { } app) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(timeout.Token);
        }),
        new(stage + "-dispose", async () => { if (endpoint() is { } app) await app.DisposeAsync(); })
    ];

    private static async Task<bool> ListenerClosedAsync(WebApplication? endpoint, int? port)
    {
        if (endpoint is null) return true;
        Check(port.HasValue, "listener-address-unverified");
        using var check = new TcpClient();
        try { await check.ConnectAsync(IPAddress.Loopback, port!.Value).WaitAsync(TimeSpan.FromSeconds(2)); return false; }
        catch (SocketException) { return true; }
    }

    private static async Task<WebApplication> StartEndpointAsync(X509Certificate2 certificate, ConcurrentDictionary<string, int> observations,
        ProbeFailures failures)
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
        catch (Exception error)
        {
            await RunWithCleanupAsync(failures, () => Task.FromException(error),
                [new("endpoint-startup-dispose", async () => await app.DisposeAsync())], [], _ => { });
            throw;
        }
    }

    internal static async Task RunChildAsync()
    {
        var directory = Environment.GetEnvironmentVariable(ChildDirectory);
        var diagnostic = new ProbeDiagnostic("child", value => AppendDiagnostic(directory, value)) { Directory = directory };
        using var scope = diagnostic.Activate();
        try { await RunChildCoreAsync(diagnostic); }
        catch (Exception error) { diagnostic.Capture(error); throw; }
        finally { diagnostic.Emit(); }
    }

    private static async Task RunChildCoreAsync(ProbeDiagnostic diagnostic)
    {
        // No keys or store writes in children. The parent passes only public certs.
        diagnostic.At(ProbeOperation.ChildInput);
        var directory = Environment.GetEnvironmentVariable(ChildDirectory)!;
        var input = Read<ChildInput>(Path.Combine(directory, "input.json"));
        diagnostic.Source = input.Source;
        diagnostic.At(ProbeOperation.ChildCertificates, Path.GetFileName(directory).Replace("child-", "", StringComparison.Ordinal));
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

        diagnostic.At(ProbeOperation.ChildApplication);
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
        diagnostic.At(ProbeOperation.ChildOcelot);
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
        diagnostic.At(ProbeOperation.ChildRequest);
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
        diagnostic.At(ProbeOperation.ChildResult);
        Write(Path.Combine(directory, "result.json"), result);
        if (input.PauseAfterReceipt) await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task<ChildResult> RunProcessAsync(string owner, string name, Uri target, string correlation,
        string? caFile, string? caDirectory, bool killAfterReceipt, ProbeFailures failures)
    {
        ProbeDiagnostic.Current?.At(ProbeOperation.ChildPrepare, name);
        var directory = Path.Combine(owner, "child-" + name); Directory.CreateDirectory(directory);
        var untrusted = name.StartsWith("untrusted-", StringComparison.Ordinal);
        Write(Path.Combine(directory, "input.json"), new ChildInput(target.AbsoluteUri, correlation,
            Path.Combine(owner, untrusted ? "untrusted-leaf.cer" : "trusted-leaf.cer"),
            Path.Combine(owner, untrusted ? "untrusted-root.cer" : "public-root.cer"), killAfterReceipt, ProbeDiagnostic.Current?.Source));
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
        ProbeDiagnostic.Current?.At(ProbeOperation.ChildLaunch, name);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("child-start");
        // Drain but never publish framework stdout/stderr. Its sanitized TRX has only
        // the generic outer failure; result.json is the allowlisted native observation.
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        ChildResult? childResult = null;
        await RunWithCleanupAsync(failures, async () =>
        {
            ProbeDiagnostic.Current?.At(ProbeOperation.ChildWait, name);
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
            ProbeDiagnostic.Current?.ChildObservation(directory, process.ExitCode);
            ProbeDiagnostic.Current?.At(ProbeOperation.ChildExit, name);
            if (!killAfterReceipt)
            {
                Check(process.ExitCode == 0, "child-nonzero-exit");
                ProbeDiagnostic.Current?.At(ProbeOperation.ChildAccounting, name);
                var trx = XDocument.Load(Path.Combine(directory, "result.trx"));
                var counters = trx.Descendants().Single(node => node.Name.LocalName == "Counters");
                Check((string?)counters.Attribute("total") == "1" && (string?)counters.Attribute("passed") == "1"
                    && (string?)counters.Attribute("executed") == "1" && (string?)counters.Attribute("notExecuted") == "0", "child-exact-test-accounting");
                ProbeDiagnostic.Current?.Counters(counters);
            }
            else Check(process.ExitCode != 0, "abnormal-child-termination-exit");
            Write(Path.Combine(directory, "process.json"), new { exitCode = process.ExitCode, killedBySupervisor = killAfterReceipt,
                expectedExecutedTests = 1, testIdentity = TestIdentity, noCredentialsInherited = true });
            ProbeDiagnostic.Current?.At(ProbeOperation.ChildResult, name);
            childResult = Read<ChildResult>(Path.Combine(directory, "result.json"));
        }, [new("child-process-kill", () =>
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return Task.CompletedTask;
            }),
            new("child-process-wait", async () => await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)))],
            [], _ => ProbeDiagnostic.Current?.ChildObservation(directory, process.HasExited ? process.ExitCode : null));
        return childResult!;
    }

    // Fixed vocabulary only: never serialize exception messages, streams, targets or input.json.
    internal enum ProbeOperation
    {
        Unknown, EnvironmentAdmission, SourceIdentity, CertificateSetup, TrustedEndpointStart,
        UntrustedEndpointStart, TrustedAddress, UntrustedAddress, ControlSocket, ControlAssertions,
        ChildInput, ChildCertificates, ChildApplication, ChildOcelot, ChildRequest, ChildResult,
        ChildPrepare, ChildLaunch, ChildWait, ChildExit, ChildAccounting
    }
    internal sealed record DiagnosticSource(string checkedOutCommit, string checkedOutTree,
        string testAssemblySha256, string? workflowHead);

    internal sealed class ProbeDiagnostic(string scope, Action<object> persist, Action<object>? output = null)
    {
        private static readonly AsyncLocal<ProbeDiagnostic?> Ambient = new();
        internal static ProbeDiagnostic? Current => Ambient.Value;
        internal string? Directory { get; set; }
        internal DiagnosticSource? Source { get; set; }
        private ProbeOperation operation;
        private string control = "none";
        private object? firstFailure;
        private readonly List<object> cleanup = [];
        private readonly List<object> observations = [];
        private readonly HashSet<string> limitations = [];
        private int? childExit;
        private bool? resultPresent, trxPresent;
        private Dictionary<string, int>? counters;
        private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
        {
            "terminated-child", "before-normal", "trusted-normal", "untrusted-normal", "after-normal",
            "before-startup-failure", "after-startup-failure", "before-abnormal-child", "trusted-abnormal-child",
            "untrusted-abnormal-child", "after-abnormal-child"
        };
        private static readonly HashSet<string> Assertions = new(StringComparer.Ordinal)
        {
            "matched-validity", "startup-failure-observed", "unexpected-TLS-outcome", "control-configuration",
            "diagnostic-chain-outcome", "certificate-specific-rejection", "correlated-downstream-count",
            "listener-address-unverified", "correlation-format", "endpoint-request", "local-HTTPS-only",
            "real-pool-registration", "normal-route-creation", "default-validation-required", "downstream-response",
            "abnormal-control-early-exit", "child-nonzero-exit", "child-exact-test-accounting",
            "abnormal-child-termination-exit", "windows-store-only", "unique-root-before-import", "root-import-observed",
            "owned-root-removed", "net8-runtime", "pinned-ocelot", "supported-platform", "hosted-windows-2022-required",
            "dedicated-hosted-account", "new-owned-evidence-directory", "source-identity"
        };
        private static readonly HashSet<string> CleanupCodes = new(StringComparer.Ordinal)
        {
            "owned-root-cleanup", "ca-file-cleanup", "ca-directory-cleanup", "endpoint-startup-dispose",
            "trusted-endpoint-address", "trusted-endpoint-stop", "trusted-endpoint-dispose",
            "untrusted-endpoint-address", "untrusted-endpoint-stop", "untrusted-endpoint-dispose",
            "child-process-kill", "child-process-wait", "root-absent", "ca-file-absent", "ca-directory-absent",
            "parent-environment-unchanged", "trusted-listener-closed", "untrusted-listener-closed"
        };
        internal IDisposable Activate()
        {
            var previous = Ambient.Value;
            Ambient.Value = this;
            return new DiagnosticScope(previous);
        }
        private sealed class DiagnosticScope(ProbeDiagnostic? previous) : IDisposable
        {
            public void Dispose() => Ambient.Value = previous;
        }
        internal void At(ProbeOperation stage, string? name = null)
        {
            operation = Enum.IsDefined(stage) ? stage : ProbeOperation.Unknown;
            if (name is not null) control = Controls.Contains(name) ? name : "unknown";
            if (stage == ProbeOperation.ChildPrepare)
            { childExit = null; resultPresent = null; trxPresent = null; counters = null; }
        }
        internal void Capture(Exception error, string? assertion = null) => firstFailure ??= new
        {
            operation = operation.ToString(), control,
            assertion = assertion is not null && Assertions.Contains(assertion) ? assertion : "unknown",
            exceptionKind = Kind(error)
        };
        private static string Kind(Exception? error) => error switch
        {
            null => "none", InvalidOperationException => "invalid-operation", SocketException => "socket",
            TimeoutException => "timeout", OperationCanceledException => "cancelled", IOException => "io",
            UnauthorizedAccessException => "access", CryptographicException => "cryptographic",
            AuthenticationException => "authentication", JsonException => "json", System.Xml.XmlException => "xml",
            _ => "unknown"
        };
        internal void Cleanup(string stage, bool succeeded, Exception? error) => cleanup.Add(new
        {
            stage = CleanupCodes.Contains(stage) ? stage : "unknown", succeeded, exceptionKind = Kind(error)
        });
        internal void ObserveCleanup(IReadOnlyDictionary<string, bool?> values)
        {
            foreach (var value in values)
                observations.Add(new { stage = CleanupCodes.Contains(value.Key) ? value.Key : "unknown", observed = value.Value });
        }
        internal void ChildObservation(string directory, int? exit)
        {
            childExit = exit;
            try
            {
                // Attributes distinguish absence from unreadable/unverifiable presence.
                resultPresent = Present(Path.Combine(directory, "result.json"));
                trxPresent = Present(Path.Combine(directory, "result.trx"));
                if (trxPresent == true)
                    Counters(XDocument.Load(Path.Combine(directory, "result.trx")).Descendants().Single(x => x.Name.LocalName == "Counters"));
            }
            catch { limitations.Add("child-observation-unavailable"); }
        }
        private static bool Present(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
        internal void Counters(XElement element)
        {
            var values = new Dictionary<string, int>();
            foreach (var key in new[] { "total", "executed", "passed", "failed", "notExecuted" })
            {
                if (!int.TryParse((string?)element.Attribute(key), out var value) || value < 0 || value > 1)
                { limitations.Add("child-counters-unverified"); return; }
                values[key] = value;
            }
            if (values["total"] != 1 || values["executed"] + values["notExecuted"] != 1
                || values["passed"] + values["failed"] != values["executed"])
            { limitations.Add("child-counters-unverified"); return; }
            counters = values;
        }
        private static string? Hash(string? value, int length) => value?.Length == length && value.All(Uri.IsHexDigit) ? value : null;
        private object Record() => new
        {
            schema = 1, phase = "diagnostic", scope = scope is "supervisor" or "child" ? scope : "unknown", test = TestIdentity,
            source = new { checkedOutCommit = Hash(Source?.checkedOutCommit, 40), checkedOutTree = Hash(Source?.checkedOutTree, 40),
                testAssemblySha256 = Hash(Source?.testAssemblySha256, 64), workflowHead = Hash(Source?.workflowHead, 40) },
            control, lastOperation = operation.ToString(), firstFailure,
            child = new { exitCode = childExit, resultPresent, trxPresent, counters },
            cleanupAttempts = cleanup, cleanupObservations = observations, evidenceLimitations = limitations.ToArray()
        };
        internal void Emit()
        {
            // Diagnostic sink failure never replaces the real primary/cleanup failure.
            try { persist(Record()); } catch { limitations.Add("diagnostic-persistence-unavailable"); }
            try { output?.Invoke(Record()); } catch { limitations.Add("diagnostic-output-unavailable"); }
            if (limitations.Count != 0)
                try { persist(Record()); } catch { /* No safe sink remains; preserve primary. */ }
        }
    }
    private static void AppendDiagnostic(string? diagnosticDirectory, object value) =>
        File.AppendAllText(Path.Combine(diagnosticDirectory ?? throw new IOException(), "diagnostic.jsonl"),
            JsonSerializer.Serialize(value, Json) + Environment.NewLine);

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

    private static void CleanupRoot(X509Certificate2 root)
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

    private static DiagnosticSource SourceIdentity()
    {
        // Git emits only immutable identities, never status, remotes, environment or credentials.
        string Git(string argument)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var value = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(); Check(process.ExitCode == 0 && value.Length == 40 && value.All(Uri.IsHexDigit), "source-identity"); return value;
        }
        return new(Git("HEAD"), Git("HEAD^{tree}"),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GatewayHandlerTrustProbeTests).Assembly.Location))),
            Environment.GetEnvironmentVariable("GITHUB_SHA"));
    }

    private static string OcelotVersion() => typeof(MessageInvokerPool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private static Uri Address(WebApplication app) => new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
    private static void Check(bool condition, string reason)
    {
        if (condition) return;
        var error = new InvalidOperationException(reason);
        ProbeDiagnostic.Current?.Capture(error, reason);
        throw error;
    }
    private static void Write<T>(string path, T value)
    {
        var temporary = path + ".pending";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json)); File.Move(temporary, path, overwrite: true);
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)!;
    private sealed class PlannedStartupFailure : Exception;
    private sealed record EnvironmentIdentity(string Boundary, string? AccountSid, string? ImageOS, string? ImageVersion);
    private sealed record ChildInput(string Target, string Correlation, string LeafFile, string IssuerFile, bool PauseAfterReceipt, DiagnosticSource? Source);
    private sealed record ChildResult(bool Accepted, bool CertificateFailure, string HttpError, bool AuthenticationException,
        bool NameValid, bool TimeValid, bool ChainValid, string[] ChainStatuses, bool RealPool);
}
