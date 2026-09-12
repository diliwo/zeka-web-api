using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Zeka.Authentication.Tests;

// Execute the service entry point in a separate process: environment overrides must
// be present before CreateBuilder, without changing the test runner's environment.
public abstract class RealHostStartupTests
{
    protected abstract string ApiDirectory { get; }
    protected abstract string AssemblyName { get; }
    protected virtual bool IsIssuer => false;

    [Theory]
    [InlineData(null)]
    [InlineData("AuthBaseAddress")]
    [InlineData("UnexpectedKey")]
    public async Task Tracked_settings_and_environment_overrides_start_only_with_known_authentication_keys(string? unexpectedKey)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "zeka-web-api.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var api = Path.Combine(root!.FullName, ApiDirectory);
        var assembly = Path.Combine(api, "bin", configuration, "net8.0", AssemblyName + ".dll");
        Assert.True(File.Exists(assembly), "Build the API before running its startup tests.");
        var directory = Directory.CreateTempSubdirectory("zeka-auth-startup-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = new Process();
        var started = false;
        try
        {
            // Copy exactly the tracked base settings, without development settings or user secrets.
            File.Copy(Path.Combine(api, "appsettings.json"), Path.Combine(directory.FullName, "appsettings.json"));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                WorkingDirectory = directory.FullName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(assembly);
            // Do not inherit developer credentials, connection strings or authentication settings.
            var inherited = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR", "DOTNET_ROOT" }
                .ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key));
            start.Environment.Clear();
            foreach (var (key, value) in inherited)
                if (value is not null) start.Environment[key] = value;
            start.Environment["HOME"] = directory.FullName;
            start.Environment["USERPROFILE"] = directory.FullName;
            start.Environment["LOCALAPPDATA"] = directory.FullName;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "StartupTest";
            start.Environment["DOTNET_ENVIRONMENT"] = "StartupTest";
            start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
            using var fixture = new AuthenticationFixture();
            foreach (var (key, value) in fixture.Configuration().AsEnumerable())
                if (value is not null) start.Environment[key.Replace(":", "__")] = value;
            start.Environment["TenantAuthorization__AuthManagementUrl"] = AuthenticationFixture.Issuer;
            start.Environment["RabbitMq__HostName"] = "127.0.0.1";
            start.Environment["EventBus__QueueName"] = "startup-test-" + Guid.NewGuid().ToString("N");
            if (IsIssuer)
            {
                var reference = Path.Combine(directory.FullName, "ephemeral-test-key.pem");
                await File.WriteAllTextAsync(reference, fixture.Key.ExportRSAPrivateKeyPem(), timeout.Token);
                start.Environment["AuthenticationIssuer__ActiveSigningKeyReference"] = reference;
                start.Environment["AuthenticationIssuer__ActiveKeyId"] = AuthenticationFixture.KeyId;
                start.Environment["AuthenticationIssuer__AccessTokenLifetime"] = "00:05:00";
            }
            if (unexpectedKey is not null)
                start.Environment["Authentication__" + unexpectedKey] = "unexpected-test-value";
            process.StartInfo = start;
            started = process.Start();
            Assert.True(started);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = new List<string>();
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                output.Add(line);
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0) continue;
                address = line[(index + marker.Length)..].Trim();
                break;
            }
            if (unexpectedKey is not null)
            {
                Assert.Null(address);
                await process.WaitForExitAsync(timeout.Token);
                Assert.NotEqual(0, process.ExitCode);
                var diagnostics = string.Join('\n', output) + await stderr;
                Assert.Contains("Invalid authentication configuration.", diagnostics);
                Assert.DoesNotContain("unexpected-test-value", diagnostics);
            }
            else
            {
                Assert.True(address is not null, string.Join('\n', output) + (process.HasExited ? await stderr : ""));
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await http.GetAsync(address + (IsIssuer ? "/.well-known/jwks.json" : "/startup-test-not-found"), timeout.Token);
                Assert.Equal(IsIssuer ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
                if (IsIssuer)
                {
                    var document = await response.Content.ReadAsStringAsync(timeout.Token);
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture.Jwks()), JsonNode.Parse(document)),
                        "The actual issuer must publish exactly the fixture's public JWKS.");
                }
                Assert.False(process.HasExited);
            }
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            directory.Delete(recursive: true);
        }
    }
}
