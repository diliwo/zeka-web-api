using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Zeka.PersistenceSecurity;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "46")]
[Trait("Evidence", "PlatformConformance")]
public sealed class PostgreSqlAuthRoleCatalogEvidenceTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly RetainedPostgreSqlFixture postgres = new(output);
    private string MigratorPassword => postgres.MigratorPassword;
    private string RuntimePassword => postgres.RuntimePassword;

    public Task InitializeAsync() => postgres.StartAsync();
    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    private static string FreshSecret() => "Zk9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    // Preserve the original component contract with the canonical fixture's custody.
    private sealed class RetainedPostgreSqlFixture(Xunit.Abstractions.ITestOutputHelper output) : IAsyncDisposable
    {
        private const string SecretPath = "/run/secrets/admin-password";
        private readonly string id = Guid.NewGuid().ToString("N");
        private IContainer? container;
        private INetwork? network;
        private IVolume? volume;
        private ushort port;
        private string adminSecret = string.Empty;
        public string MigratorPassword { get; private set; } = string.Empty;
        public string RuntimePassword { get; private set; } = string.Empty;
        private ExternalFixtureConfiguration? external;
        private string? externalRunId;
        private string stage = "fixture-construction";
        private FixtureFailure? firstFailure;
        private string Project => "zeka-auth-role-catalog-" + id;

        public async Task StartAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var ct = deadline.Token;
            try
            {
                stage = "fixture-transport-selection";
                var selection = SelectExternalFixture(
                    Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_FIXTURE_MODE"),
                    Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_FIXTURE_CONFIG_FILE"),
                    Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_FIXTURE_RUN_ID"),
                    Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_FIXTURE_SOURCE_TREE"),
                    Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_CATALOG_SNAPSHOT_FILE"));
                if (selection is not null)
                {
                    externalRunId = selection.RunId;
                    await StartExternalAsync(selection, ct);
                    return;
                }
                adminSecret = FreshSecret();
                MigratorPassword = FreshSecret();
                RuntimePassword = FreshSecret();
                var labels = new Dictionary<string, string>
                {
                    ["com.docker.compose.project"] = Project,
                    ["com.diliwo.zeka.fixture-id"] = id,
                    ["com.diliwo.zeka.scope"] = "auth-role-catalog-component-tests",
                    ["com.diliwo.zeka.owner"] = "codex"
                };
                stage = "owned-network-volume";
                network = new NetworkBuilder().WithName(Project + "-private").WithLabel(labels)
                    .WithCleanUp(false).WithCreateParameterModifier(value => value.Internal = false).Build();
                await network.CreateAsync(ct);
                volume = new VolumeBuilder().WithName(Project + "-data").WithLabel(labels).WithCleanUp(false).Build();
                await volume.CreateAsync(ct);
                var bytes = Encoding.UTF8.GetBytes(adminSecret);
                try
                {
                    container = new ContainerBuilder().WithImage("postgres:17-alpine").WithName(Project + "-postgres")
                        .WithLabel(labels).WithNetwork(network).WithCleanUp(false).WithAutoRemove(false)
                        .WithEnvironment("POSTGRES_USER", "postgres").WithEnvironment("POSTGRES_PASSWORD_FILE", SecretPath)
                        .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256")
                        .WithResourceMapping(bytes, SecretPath, uid: 70, gid: 70, fileMode: UnixFileModes.UserRead)
                        .WithVolumeMount(volume, "/var/lib/postgresql/data", AccessMode.ReadWrite)
                        .WithPortBinding(5432, true).WithCreateParameterModifier(parameters =>
                        {
                            if (parameters.HostConfig.PortBindings.Count != 1)
                                throw new InvalidOperationException("AUTH_FIXTURE_PORT_BOUNDARY_REJECTED");
                            foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                            foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
                        })
                        .WithCommand("-c", "log_statement=none", "-c", "log_min_error_statement=panic")
                        .WithLogger(NullLogger.Instance)
                        .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                            ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres"]))
                        .Build();
                    stage = "owned-container-start";
                    await container.StartAsync(ct);
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                port = container.GetMappedPublicPort(5432);
                stage = "actual-resource-custody";
                var actual = await InspectAsync(ct);
                Require(actual.GetProperty("id").GetString() == container.Id
                    && actual.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString() == id
                    && actual.GetProperty("labels").GetProperty("com.docker.compose.project").GetString() == Project
                    && !actual.GetProperty("privileged").GetBoolean(), "AUTH_FIXTURE_IDENTITY_REJECTED");
                var ports = actual.GetProperty("ports").EnumerateObject().ToArray();
                Require(ports.Length == 1 && ports[0].Name == "5432/tcp" && ports[0].Value.GetArrayLength() == 1
                    && ports[0].Value[0].GetProperty("HostIp").GetString() == "127.0.0.1"
                    && ports[0].Value[0].GetProperty("HostPort").GetString() == port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "AUTH_FIXTURE_PORT_BOUNDARY_REJECTED");
                var mounts = actual.GetProperty("mounts").EnumerateArray().ToArray();
                Require(mounts.Length == 1 && mounts[0].GetProperty("Type").GetString() == "volume"
                    && mounts[0].GetProperty("Name").GetString() == volume.Name
                    && mounts[0].GetProperty("Destination").GetString() == "/var/lib/postgresql/data"
                    && mounts[0].GetProperty("RW").GetBoolean()
                    && actual.GetProperty("networks").EnumerateObject().Select(value => value.Name)
                        .SequenceEqual(new[] { network.Name }, StringComparer.Ordinal), "AUTH_FIXTURE_RESOURCE_BOUNDARY_REJECTED");
                var metadata = await container.ExecAsync(["stat", "-c", "%u:%g:%a:%F", SecretPath], ct);
                var environment = await container.ExecAsync(["/bin/sh", "-c",
                    """test -z "${POSTGRES_PASSWORD+x}" && test "$POSTGRES_PASSWORD_FILE" = /run/secrets/admin-password"""], ct);
                Require(metadata.ExitCode == 0 && metadata.Stdout.Trim() == "70:70:400:regular file"
                    && environment.ExitCode == 0, "AUTH_FIXTURE_PRIVATE_FILE_REJECTED");
                await ValidateFreshPostgreSqlAsync(ct);
                output.WriteLine(JsonSerializer.Serialize(new { fixtureId = id, project = Project, containerId = container.Id,
                    image = actual.GetProperty("image").GetString(), network = network.Name, volume = volume.Name,
                    hostBinding = "127.0.0.1", port, defaultDurability = true,
                    credentialDelivery = "copied-private-file-uid70-mode0400", retainedAfterStop = true, reaperCleanupClaimed = false }));
            }
            catch (Exception error) { throw SafeFailure(error); }
        }

        private async Task ValidateFreshPostgreSqlAsync(CancellationToken ct)
        {
                stage = "fresh-postgresql-default-durability";
                await using var connection = new NpgsqlConnection(GetConnectionString());
                await connection.OpenAsync(ct);
                await using var command = new NpgsqlCommand("""
                SELECT current_setting('server_version_num')::integer/10000=17
                  AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on'
                  AND current_setting('synchronous_commit')='on'
                  AND session_user='postgres' AND current_user='postgres' AND current_database()='postgres'
                  AND current_setting('log_statement')='none' AND current_setting('log_min_error_statement')='panic'
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_database WHERE datname NOT IN ('postgres','template0','template1'))
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_')
                """, connection);
                Require(await command.ExecuteScalarAsync(ct) is true, "AUTH_FIXTURE_FRESH_PG17_DURABILITY_REJECTED");
        }

        public string GetConnectionString() => new NpgsqlConnectionStringBuilder
        {
            Host = external?.Host ?? "127.0.0.1", Port = port, Database = "postgres", Username = "postgres", Password = adminSecret,
            Pooling = false, Timeout = 5, CommandTimeout = 30, IncludeErrorDetail = false
        }.ConnectionString;

        public async ValueTask DisposeAsync()
        {
            // External custody owns no Docker resources; every Npgsql connection uses await using and Pooling=false.
            if (externalRunId is not null) return;
            // Stop only; never remove the owned container, network or data volume.
            if (container is null) return;
            stage = "stop-owned-container-retain-resources";
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await container.StopAsync(stop.Token); }
            catch (Exception error) { throw SafeFailure(error); }
        }


        private async Task StartExternalAsync(ExternalFixtureSelection selection, CancellationToken ct)
        {
            stage = "external-linux-private-inputs";
            RequireExternal(OperatingSystem.IsLinux(), "AUTH_EXTERNAL_LINUX_REQUIRED");
            RequireExternal(await FileGuardCommandAsync("/usr/bin/id", ["-u"], ct) == "1654\n",
                "AUTH_EXTERNAL_UID_REJECTED");
            await VerifyPrivatePathAsync(selection.SecretDirectory, true, 0, ct);
            await VerifyPrivatePathAsync(selection.ResultDirectory, true, 0, ct);
            RequireExternal(!Path.Exists(selection.SnapshotFile), "AUTH_EXTERNAL_OUTPUT_EXISTS");
            var configBytes = await ReadPrivateFileAsync(selection.ConfigFile, 16384, ct);
            try { external = ParseExternalConfiguration(configBytes, selection); }
            finally { CryptographicOperations.ZeroMemory(configBytes); }
            stage = "external-loaded-assembly-identity";
            var producer = CatalogAssemblyIdentity.Read(typeof(PostgreSqlAuthRoleCatalogEvidenceTests).Assembly);
            var ef = CatalogAssemblyIdentity.Read(typeof(DbContext).Assembly);
            var npgsql = CatalogAssemblyIdentity.Read(typeof(NpgsqlConnection).Assembly);
            RequireExternal(producer.Sha256 == external.ExpectedTestAssemblySha256
                && InformationalPackageVersion(ef.InformationalVersion) == "8.0.8"
                && InformationalPackageVersion(npgsql.InformationalVersion) == "8.0.4",
                "AUTH_EXTERNAL_ASSEMBLY_IDENTITY_REJECTED");
            var compiledCommit = producer.InformationalVersion.Split('+', 2).ElementAtOrDefault(1);
            if (compiledCommit is not null)
                RequireExternal(compiledCommit == external.SourceCommit, "AUTH_EXTERNAL_COMPILED_COMMIT_REJECTED");
            stage = "external-private-credentials";
            adminSecret = await ReadExternalPasswordAsync(external.AdminPasswordFile, ct);
            MigratorPassword = await ReadExternalPasswordAsync(external.MigratorPasswordFile, ct);
            RuntimePassword = await ReadExternalPasswordAsync(external.RuntimePasswordFile, ct);
            RequireExternal(adminSecret != MigratorPassword && adminSecret != RuntimePassword
                && MigratorPassword != RuntimePassword, "AUTH_EXTERNAL_CREDENTIAL_REUSE_REJECTED");
            port = 5432;
            stage = "external-empty-provider-catalog";
            await using (var connection = new NpgsqlConnection(GetConnectionString()))
            {
                await connection.OpenAsync(ct);
                using var schemata = await connection.GetSchemaAsync("Schemata", ct);
                using var tables = await connection.GetSchemaAsync("Tables", ct);
                ValidateEmptyExternalCatalog(schemata, tables);
            }
            // Reuse the exact existing PG17/identity/durability admission before any original bootstrap.
            await ValidateFreshPostgreSqlAsync(ct);
            output.WriteLine(JsonSerializer.Serialize(new { fixtureId = selection.RunId, transport = "external-file",
                resourceOwner = "windows-host", host = external.Host, port, defaultDurability = true,
                credentialDelivery = "linux-local-private-files-uid1654-mode0400", disposal = "own-connections-only",
                sourceCommit = external.SourceCommit, sourceTree = external.SourceTree,
                compiledSourceCommit = compiledCommit, compiledSourceCommitObserved = compiledCommit is not null,
                producerAssembly = producer, entityFramework = ef, npgsql,
                hostTargetAndNetworkAdmissionRequired = true }));
        }

        private async Task<string> ReadExternalPasswordAsync(string path, CancellationToken ct)
        {
            var bytes = await ReadPrivateFileAsync(path, 128, ct);
            try
            {
                // Original ALTER ROLE SQL is unchanged; reject every character needing SQL escaping.
                RequireExternal(bytes.Length >= 32 && bytes.All(value => value is >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                    or (byte)'!' or (byte)'_' or (byte)'-'), "AUTH_EXTERNAL_CREDENTIAL_FORMAT_REJECTED");
                return Encoding.ASCII.GetString(bytes);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }


        private const string PrivateMetadataFormat = "%u:%g:%a:%f:%h:%s:%d:%i:%Y:%Z|%y|%z";

        private async Task<byte[]> ReadPrivateFileAsync(string path, int maximumBytes, CancellationToken ct)
        {
            var before = await VerifyPrivatePathAsync(path, false, maximumBytes, ct);
            var bytes = new byte[maximumBytes + 1];
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 4096, FileOptions.Asynchronous);
                var handle = stream.SafeFileHandle;
                var retained = false;
                try
                {
                    // Pin the actual opened descriptor before reading, rather than trusting pathname restats.
                    handle.DangerousAddRef(ref retained);
                    var descriptorPath = ExternalDescriptorPath(Environment.ProcessId, handle.DangerousGetHandle().ToInt64());
                    var openedBefore = await VerifyOpenedPrivateFileAsync(path, descriptorPath, maximumBytes, ct);
                    ValidatePrivateFileIdentity(before, openedBefore);
                    var count = 0;
                    while (count < bytes.Length)
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
                        if (read == 0) break;
                        count += read;
                    }
                    RequireExternal(count is > 0 && count <= maximumBytes, "AUTH_EXTERNAL_PRIVATE_FILE_SIZE_REJECTED");
                    var openedAfter = await VerifyOpenedPrivateFileAsync(path, descriptorPath, maximumBytes, ct);
                    var after = await VerifyPrivatePathAsync(path, false, maximumBytes, ct);
                    ValidatePrivateFileIdentity(before, openedAfter);
                    ValidatePrivateFileIdentity(before, after);
                    RequireExternal(stream.Length == count, "AUTH_EXTERNAL_PRIVATE_FILE_CHANGED");
                    return bytes[..count];
                }
                finally { if (retained) handle.DangerousRelease(); }
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private async Task<string> VerifyOpenedPrivateFileAsync(string path, string descriptorPath, int maximumBytes,
            CancellationToken ct)
        {
            ValidateCanonicalExternalPath(path, await FileGuardCommandAsync("/usr/bin/readlink", ["-e", "--", descriptorPath], ct));
            var metadata = await FileGuardCommandAsync("/usr/bin/stat",
                ["-L", "--printf", PrivateMetadataFormat, "--", descriptorPath], ct);
            ValidatePrivateMetadata(metadata, false, maximumBytes);
            return metadata;
        }

        private async Task<string> VerifyPrivatePathAsync(string path, bool directory, int maximumBytes, CancellationToken ct)
        {
            ValidateCanonicalExternalPath(path, await FileGuardCommandAsync("/usr/bin/readlink", ["-e", "--", path], ct));
            var metadata = await FileGuardCommandAsync("/usr/bin/stat",
                ["--printf", PrivateMetadataFormat, "--", path], ct);
            ValidatePrivateMetadata(metadata, directory, maximumBytes);
            return metadata;
        }

        private async Task<string> FileGuardCommandAsync(string executable, string[] arguments, CancellationToken ct)
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true };
            start.Environment["LC_ALL"] = "C";
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            var nativeStarted = false;
            var recorded = false;
            Task<string>? stdout = null, stderr = null;
            try
            {
                nativeStarted = process.Start();
                if (!nativeStarted)
                {
                    RecordNative(false, null, "", "", true, true);
                    recorded = true;
                    throw new InvalidOperationException("AUTH_EXTERNAL_FILE_GUARD_NOT_STARTED");
                }
                stdout = process.StandardOutput.ReadToEndAsync(ct);
                stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                var observed = await stdout;
                var error = await stderr;
                RecordNative(true, process.ExitCode, observed, error, true, true);
                recorded = true;
                RequireExternal(process.ExitCode == 0 && observed.Length <= 4096 && error.Length == 0,
                    "AUTH_EXTERNAL_FILE_GUARD_FAILED");
                return observed;
            }
            catch
            {
                if (nativeStarted) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
                if (!recorded) RecordNative(nativeStarted, nativeStarted && process.HasExited ? process.ExitCode : null,
                    stdout?.IsCompletedSuccessfully == true ? stdout.Result : "[NATIVE_STDOUT_INCOMPLETE]",
                    stderr?.IsCompletedSuccessfully == true ? stderr.Result : "[NATIVE_STDERR_INCOMPLETE]",
                    stdout?.IsCompletedSuccessfully == true, stderr?.IsCompletedSuccessfully == true);
                throw;
            }
        }

        private async Task<JsonElement> InspectAsync(CancellationToken ct)
        {
            const string format = """{"id":{{json .Id}},"image":{{json .Image}},"labels":{{json .Config.Labels}},"ports":{{json .NetworkSettings.Ports}},"mounts":{{json .Mounts}},"networks":{{json .NetworkSettings.Networks}},"privileged":{{json .HostConfig.Privileged}}}""";
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "inspect", "--format", format, container!.Id }) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            var nativeStarted = false;
            var recorded = false;
            Task<string>? stdout = null, stderr = null;
            try
            {
                nativeStarted = process.Start();
                if (!nativeStarted)
                {
                    RecordNative(false, null, "", "", true, true);
                    recorded = true;
                    throw new InvalidOperationException("AUTH_FIXTURE_INSPECTION_NOT_STARTED");
                }
                stdout = process.StandardOutput.ReadToEndAsync(ct);
                stderr = process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                var observed = await stdout;
                var error = await stderr;
                RecordNative(true, process.ExitCode, observed, error, true, true);
                recorded = true;
                Require(process.ExitCode == 0 && observed.Length <= 65536, "AUTH_FIXTURE_INSPECTION_FAILED");
                using var document = JsonDocument.Parse(observed, new JsonDocumentOptions { MaxDepth = 16 });
                return document.RootElement.Clone();
            }
            catch
            {
                if (nativeStarted) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }
                if (!recorded) RecordNative(nativeStarted, nativeStarted && process.HasExited ? process.ExitCode : null,
                    stdout?.IsCompletedSuccessfully == true ? stdout.Result : "[NATIVE_STDOUT_INCOMPLETE]",
                    stderr?.IsCompletedSuccessfully == true ? stderr.Result : "[NATIVE_STDERR_INCOMPLETE]",
                    stdout?.IsCompletedSuccessfully == true, stderr?.IsCompletedSuccessfully == true);
                throw;
            }
        }

        private void RecordNative(bool nativeStarted, int? nativeExit, string stdout, string stderr,
            bool stdoutComplete, bool stderrComplete)
        {
            var safeOut = ProtectFixtureText(stdout, adminSecret, 4096, MigratorPassword, RuntimePassword);
            var safeError = ProtectFixtureText(stderr, adminSecret, 4096, MigratorPassword, RuntimePassword);
            output.WriteLine(JsonSerializer.Serialize(new { fixtureId = externalRunId ?? id, project = externalRunId is null ? Project : null, stage, nativeStarted, nativeExit,
                stdout = safeOut.Text, stdoutTruncated = safeOut.Truncated, stdoutComplete,
                stderr = safeError.Text, stderrTruncated = safeError.Truncated, stderrComplete }));
        }

        private InvalidOperationException SafeFailure(Exception error)
        {
            var first = CaptureFixtureFailure(ref firstFailure, error, stage, adminSecret, MigratorPassword, RuntimePassword);
            output.WriteLine(JsonSerializer.Serialize(new { fixtureId = externalRunId ?? id, project = externalRunId is null ? Project : null, firstStage = first.Stage,
                observedStage = stage, errorCategory = first.ErrorCategory, message = first.Message,
                messageTruncated = first.MessageTruncated, postgresDiagnostic = first.PostgresDiagnostic,
                postgresMessageTruncated = first.PostgresMessageTruncated, postgresHintTruncated = first.PostgresHintTruncated,
                retainedAfterStop = externalRunId is null, reaperCleanupClaimed = false,
                resourceOwner = externalRunId is null ? "testcontainers" : "windows-host" }));
            // No provider exception, DETAIL, query, parameters or raw inner exception is exported.
            return new InvalidOperationException("AUTH_ROLE_CATALOG_FIXTURE_FAILED/" + first.Stage + "/" + first.ErrorCategory);
        }

        private static void Require(bool condition, string code)
        {
            if (!condition) throw new InvalidOperationException(code);
        }
    }

    private sealed record FixtureText(string Text, bool Truncated);
    private sealed record FixturePostgresDiagnostic(
        [property: JsonPropertyName("sqlState")] string SqlState,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("hint")] string? Hint,
        [property: JsonPropertyName("position")] int Position);
    private sealed record FixtureFailure(string Stage, string ErrorCategory, string Message, bool MessageTruncated,
        FixturePostgresDiagnostic? PostgresDiagnostic, bool PostgresMessageTruncated, bool PostgresHintTruncated);

    private static FixtureText ProtectFixtureText(string value, string knownSecret, int limit, params string[] otherSecrets)
    {
        foreach (var secret in otherSecrets.Prepend(knownSecret))
            if (secret.Length > 0) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        var safe = FixtureSafePostgresText.Protect(value);
        return new(safe.Length <= limit ? safe : safe[..limit], safe.Length > limit);
    }

    private static FixtureFailure CaptureFixtureFailure(ref FixtureFailure? first, Exception error, string stage, string secret, params string[] otherSecrets)
    {
        var original = error;
        PostgresException? postgres = null;
        for (var depth = 0; depth < 16 && error is not null; depth++, error = error.InnerException!)
            if (error is PostgresException pg) { postgres = pg; break; }
        // Only the server's approved primary field is used for a PostgreSQL exception,
        // never Exception.Message, which can combine DETAIL or other provider fields.
        var message = ProtectFixtureText(postgres?.MessageText ?? original.Message, secret, 2048, otherSecrets);
        var hint = postgres?.Hint is { } value ? ProtectFixtureText(value, secret, 2048, otherSecrets) : null;
        var captured = new FixtureFailure(stage, original.GetType().Name,
            message.Text, message.Truncated,
            postgres is null ? null : new(postgres.SqlState, message.Text, hint?.Text, postgres.Position),
            postgres is not null && message.Truncated, hint?.Truncated ?? false);
        return Interlocked.CompareExchange(ref first, captured, null) ?? captured;
    }

    public sealed class FixtureDiagnosticTests
    {
        [Fact]
        public void Runtime_canaries_are_redacted_and_only_four_PostgreSql_fields_leave_the_fixture()
        {
            var knownSecret = Guid.NewGuid().ToString("N");
            var credential = Guid.NewGuid().ToString("N");
            var bearer = Guid.NewGuid().ToString("N");
            var detail = Guid.NewGuid().ToString("N");
            var raw = $"operator fixture bare:{knownSecret}; password={credential}; Bearer {bearer}; Host=synthetic;Username=fixture;Pwd={credential}";
            var postgres = new PostgresException(raw, "ERROR", "ERROR", "42883", detail: detail, hint: raw, position: 17);
            FixtureFailure? first = null;
            var projected = CaptureFixtureFailure(ref first, postgres, "first-stage", knownSecret);
            Assert.Same(projected, CaptureFixtureFailure(ref first, new InvalidOperationException(raw), "cleanup-stage", knownSecret));
            Assert.Equal("first-stage", projected.Stage);
            Assert.NotNull(projected.PostgresDiagnostic);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(projected.PostgresDiagnostic));
            Assert.Equal(new[] { "hint", "message", "position", "sqlState" },
                document.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal("42883", document.RootElement.GetProperty("sqlState").GetString());
            Assert.Equal(17, document.RootElement.GetProperty("position").GetInt32());
            Assert.Contains("operator fixture", document.RootElement.GetProperty("message").GetString());
            FixtureFailure? genericFirst = null;
            var generic = CaptureFixtureFailure(ref genericFirst, new InvalidOperationException(raw), "generic-stage", knownSecret);
            Assert.Null(generic.PostgresDiagnostic);
            Assert.Contains("operator fixture", generic.Message);
            var safe = JsonSerializer.Serialize(new { projected, generic, stdout = ProtectFixtureText(raw, knownSecret, 4096),
                stderr = ProtectFixtureText(raw, knownSecret, 4096) });
            foreach (var canary in new[] { knownSecret, credential, bearer, detail }) Assert.DoesNotContain(canary, safe);
            var bounded = ProtectFixtureText(new string('x', 5000), knownSecret, 4096);
            Assert.True(bounded.Truncated);
            Assert.Equal(4096, bounded.Text.Length);
        }
    }

    internal static class FixtureSafePostgresText
    {
        private static readonly Regex[] Rules = new[] {
            "(?i)(?:host|server|data source)\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^;\\r\\n]+)(?:\\s*;\\s*[a-z_ ]+\\s*=\\s*(?:\"[^\"]*\"|'[^']*'|[^;\\r\\n]+))*",
            "(?i)\\b(?:password|passwd|pwd|token|secret|access_token|refresh_token|client_secret|api_key|bearer_token)\\b\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s;,\\r\\n]+)",
            "(?i)\\bBearer\\s+[^\\s;,\\r\\n]+",
            "(?:gh[pousr]_|github_pat_|sqa_|squ_|sqp_|sk_live_|sk_test_)[A-Za-z0-9_=-]{8,}",
            "[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]{8,}",
            "(?i)[A-Z0-9._%+-]+@[A-Z0-9.-]+\\.[A-Z]{2,}",
            "(?i)[a-z][a-z0-9+.-]*://[^\\s/@:]+:[^\\s/@]+@[^\\s]+",
            "[A-Za-z]:[\\\\/][^;\\r\\n]+",
            "\\\\\\\\[^;\\r\\n]+",
            "/(?:[^/\\s;,]+/)+[^;\\r\\n]*",
            "'(?:''|[^'])*(?:'|$)",
            "\"(?:\"\"|[^\"])*(?:\"|$)"
        }.Select(pattern => new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(100))).ToArray();
        internal static string Protect(string value)
        {
            try { foreach (var rule in Rules) value = rule.Replace(value, "[REDACTED]"); return value; }
            catch (RegexMatchTimeoutException) { return "[POSTGRES_TEXT_WITHHELD]"; }
        }
    }


    private sealed record ExternalFixtureSelection(string RunId, string ConfigFile, string SourceTree, string SnapshotFile)
    {
        public string SecretDirectory => "/run/secrets/auth-ef8-" + RunId;
        public string ResultDirectory => "/run/results/auth-ef8-" + RunId;
    }

    private sealed record ExternalFixtureConfiguration(int SchemaVersion, string RunId, string Host, int Port,
        string Database, string Username, string AdminPasswordFile, string MigratorPasswordFile,
        string RuntimePasswordFile, string SnapshotFile, string SourceCommit, string SourceTree,
        string ExpectedTestAssemblySha256);

    private static ExternalFixtureSelection? SelectExternalFixture(string? mode, string? configFile, string? runId,
        string? sourceTree, string? snapshotFile)
    {
        // The pre-existing optional snapshot remains available with normal local Testcontainers custody.
        if (mode is null && configFile is null && runId is null && sourceTree is null) return null;
        RequireExternal(mode == "external-file", "AUTH_EXTERNAL_MODE_REJECTED");
        RequireExternal(IsLowerHex(runId, 32), "AUTH_EXTERNAL_RUN_ID_REJECTED");
        RequireExternal(IsLowerHex(sourceTree, 40), "AUTH_EXTERNAL_SOURCE_TREE_REJECTED");
        RequireExternal(configFile == "/run/secrets/auth-ef8-" + runId + "/request.json"
            && snapshotFile == "/run/results/auth-ef8-" + runId + "/reference.json",
            "AUTH_EXTERNAL_RUN_PATH_REJECTED");
        return new(runId!, configFile!, sourceTree!, snapshotFile!);
    }

    private static ExternalFixtureConfiguration ParseExternalConfiguration(byte[] bytes, ExternalFixtureSelection selection)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        RequireExternal(document.RootElement.ValueKind == JsonValueKind.Object, "AUTH_EXTERNAL_CONFIG_SHAPE_REJECTED");
        string[] names = ["schemaVersion", "runId", "host", "port", "database", "username", "adminPasswordFile",
            "migratorPasswordFile", "runtimePasswordFile", "snapshotFile", "sourceCommit", "sourceTree",
            "expectedTestAssemblySha256"];
        RequireExternal(document.RootElement.EnumerateObject().Select(value => value.Name).OrderBy(value => value,
            StringComparer.Ordinal).SequenceEqual(names.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal),
            "AUTH_EXTERNAL_CONFIG_SHAPE_REJECTED");
        var config = JsonSerializer.Deserialize<ExternalFixtureConfiguration>(bytes,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
        RequireExternal(config is not null, "AUTH_EXTERNAL_CONFIG_SHAPE_REJECTED");
        ValidateExternalConfiguration(config!, selection);
        return config!;
    }

    private static void ValidateExternalConfiguration(ExternalFixtureConfiguration config, ExternalFixtureSelection selection)
    {
        RequireExternal(config.SchemaVersion == 1 && config.RunId == selection.RunId,
            "AUTH_EXTERNAL_CONFIG_RUN_REJECTED");
        RequireExternal(config.Host == "postgres-" + selection.RunId && config.Port == 5432
            && config.Database == "postgres" && config.Username == "postgres", "AUTH_EXTERNAL_TARGET_REJECTED");
        RequireExternal(config.AdminPasswordFile == selection.SecretDirectory + "/admin-password"
            && config.MigratorPasswordFile == selection.SecretDirectory + "/migrator-password"
            && config.RuntimePasswordFile == selection.SecretDirectory + "/runtime-password"
            && config.SnapshotFile == selection.SnapshotFile, "AUTH_EXTERNAL_CONFIG_PATH_REJECTED");
        RequireExternal(config.SourceCommit == "29a299f770481fe3580be18a0fbdba3043ecc884"
            && config.SourceTree == selection.SourceTree && IsLowerHex(config.ExpectedTestAssemblySha256, 64),
            "AUTH_EXTERNAL_CONFIG_SOURCE_REJECTED");
    }

    private static void ValidateCanonicalExternalPath(string path, string observed) =>
        RequireExternal(observed == path + "\n", "AUTH_EXTERNAL_SYMLINK_OR_PATH_REJECTED");


    private static string ExternalDescriptorPath(int processId, long descriptor)
    {
        RequireExternal(processId > 0 && descriptor is > 2 and <= int.MaxValue,
            "AUTH_EXTERNAL_DESCRIPTOR_REJECTED");
        return "/proc/" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "/fd/" + descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ValidatePrivateFileIdentity(string admitted, string opened) =>
        RequireExternal(admitted == opened, "AUTH_EXTERNAL_PRIVATE_FILE_IDENTITY_CHANGED");

    private static void ValidatePrivateMetadata(string metadata, bool directory, int maximumBytes)
    {
        var parts = metadata.Split('|');
        RequireExternal(parts.Length == 3, "AUTH_EXTERNAL_PRIVATE_FILE_METADATA_REJECTED");
        var fields = parts[0].Split(':');
        RequireExternal(fields.Length == 10 && fields[0] == "1654" && fields[2] == (directory ? "700" : "400")
            && long.TryParse(fields[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _),
            "AUTH_EXTERNAL_PRIVATE_FILE_METADATA_REJECTED");
        RequireExternal(uint.TryParse(fields[3], System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var mode)
            && (mode & 0xf000) == (directory ? 0x4000 : 0x8000), "AUTH_EXTERNAL_PRIVATE_FILE_TYPE_REJECTED");
        RequireExternal(fields.Skip(4).All(value => long.TryParse(value,
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number > 0), "AUTH_EXTERNAL_PRIVATE_FILE_METADATA_REJECTED");
        const string timestamp = @"\A[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{9} [+-][0-9]{4}\z";
        RequireExternal(parts.Skip(1).All(value => Regex.IsMatch(value, timestamp,
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100))),
            "AUTH_EXTERNAL_PRIVATE_FILE_METADATA_REJECTED");
        RequireExternal(directory || fields[4] == "1" && long.Parse(fields[5], System.Globalization.CultureInfo.InvariantCulture)
            <= maximumBytes, "AUTH_EXTERNAL_PRIVATE_FILE_SIZE_OR_LINK_REJECTED");
    }

    private static void ValidateEmptyExternalCatalog(System.Data.DataTable schemata, System.Data.DataTable tables)
    {
        RequireExternal(schemata.Columns.Contains("schema_name") && tables.Columns.Contains("table_schema"),
            "AUTH_EXTERNAL_PROVIDER_SCHEMA_SHAPE_REJECTED");
        var names = schemata.Rows.Cast<System.Data.DataRow>().Select(row => row["schema_name"] as string).ToArray();
        RequireExternal(names.Length is >= 3 and <= 4 && names.Contains("public", StringComparer.Ordinal)
            && names.Contains("pg_catalog", StringComparer.Ordinal) && names.Contains("information_schema", StringComparer.Ordinal)
            && names.All(name => name is "public" or "pg_catalog" or "information_schema" or "pg_toast"),
            "AUTH_EXTERNAL_PREPOPULATED_SCHEMA_REJECTED");
        RequireExternal(tables.Rows.Cast<System.Data.DataRow>().All(row => row["table_schema"] is string schema
            && schema is "pg_catalog" or "information_schema" or "pg_toast"),
            "AUTH_EXTERNAL_PREPOPULATED_TABLE_REJECTED");
    }

    private static string InformationalPackageVersion(string value) => value.Split('+', 2)[0];
    private static bool IsLowerHex(string? value, int length) => value is not null && value.Length == length
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void RequireExternal(bool condition, string code)
    {
        if (!condition) throw new InvalidOperationException(code);
    }

    public sealed class ExternalFileFixtureContractTests
    {
        private const string Run = "0123456789abcdef0123456789abcdef";
        private static readonly string Tree = new('a', 40);
        private static ExternalFixtureSelection Selection() => SelectExternalFixture("external-file",
            "/run/secrets/auth-ef8-" + Run + "/request.json", Run, Tree,
            "/run/results/auth-ef8-" + Run + "/reference.json")!;
        private static ExternalFixtureConfiguration Configuration() => new(1, Run, "postgres-" + Run, 5432,
            "postgres", "postgres", Selection().SecretDirectory + "/admin-password",
            Selection().SecretDirectory + "/migrator-password", Selection().SecretDirectory + "/runtime-password",
            Selection().SnapshotFile, "29a299f770481fe3580be18a0fbdba3043ecc884", Tree, new string('b', 64));

        [Fact]
        public void Unset_transport_preserves_normal_local_custody_and_optional_snapshot()
        {
            Assert.Null(SelectExternalFixture(null, null, null, null, null));
            Assert.Null(SelectExternalFixture(null, null, null, null, "/local/reference.json"));
        }

        [Theory]
        [InlineData(null, "/request.json", null, null)]
        [InlineData(null, null, "0123456789abcdef0123456789abcdef", null)]
        [InlineData(null, null, null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
        [InlineData("", null, null, null)]
        [InlineData("local", null, null, null)]
        [InlineData("external-file", null, null, null)]
        public void Partial_or_unknown_external_selection_never_falls_back(string? mode, string? path, string? run,
            string? tree) => Assert.Throws<InvalidOperationException>(() => SelectExternalFixture(mode, path, run, tree, null));

        [Theory]
        [InlineData("01234567-89ab-cdef-0123-456789abcdef")]
        [InlineData("0123456789ABCDEF0123456789ABCDEF")]
        [InlineData("../0123456789abcdef0123456789abcd")]
        [InlineData("")]
        public void Noncanonical_run_identifiers_are_rejected(string run) => Assert.Throws<InvalidOperationException>(() =>
            SelectExternalFixture("external-file", Selection().ConfigFile, run, Tree, Selection().SnapshotFile));

        [Fact]
        public void Run_paths_and_independent_source_tree_are_bound()
        {
            var selection = Selection();
            Assert.Throws<InvalidOperationException>(() => SelectExternalFixture("external-file",
                selection.ConfigFile + "/../request.json", Run, Tree, selection.SnapshotFile));
            Assert.Throws<InvalidOperationException>(() => SelectExternalFixture("external-file",
                selection.ConfigFile, Run, new string('A', 40), selection.SnapshotFile));
            Assert.Throws<InvalidOperationException>(() => SelectExternalFixture("external-file",
                selection.ConfigFile, Run, Tree, "/run/results/auth-ef8-other/reference.json"));
            Assert.Throws<InvalidOperationException>(() => ValidateExternalConfiguration(Configuration() with
                { SourceTree = new string('c', 40) }, selection));
        }

        [Fact]
        public void Wrong_database_target_run_credentials_output_and_source_are_rejected()
        {
            var config = Configuration();
            ValidateExternalConfiguration(config, Selection());
            foreach (var invalid in new[] { config with { SchemaVersion = 2 }, config with { RunId = new string('0', 32) },
                config with { Host = "localhost" }, config with { Host = "postgres-other" }, config with { Port = 5433 },
                config with { Database = "auth" }, config with { Username = "owner" },
                config with { AdminPasswordFile = "/tmp/admin-password" },
                config with { MigratorPasswordFile = config.AdminPasswordFile },
                config with { RuntimePasswordFile = config.AdminPasswordFile },
                config with { SnapshotFile = "/tmp/reference.json" }, config with { SourceCommit = new string('0', 40) },
                config with { ExpectedTestAssemblySha256 = "missing" } })
                Assert.Throws<InvalidOperationException>(() => ValidateExternalConfiguration(invalid, Selection()));
        }

        [Fact]
        public void Public_config_rejects_missing_duplicate_unknown_or_wrongly_typed_fields()
        {
            var json = JsonSerializer.Serialize(Configuration(), new JsonSerializerOptions
                { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            _ = ParseExternalConfiguration(Encoding.UTF8.GetBytes(json), Selection());
            foreach (var invalid in new[] { "{}", "[]", json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1",
                StringComparison.Ordinal), json[..^1] + ",\"unexpected\":true}",
                json.Replace("\"port\":5432", "\"port\":\"5432\"", StringComparison.Ordinal) })
                Assert.ThrowsAny<Exception>(() => ParseExternalConfiguration(Encoding.UTF8.GetBytes(invalid), Selection()));
        }

        [Theory]
        [InlineData("0:1654:400:8100:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:440:8120:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:600:8180:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:400:a100:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:400:8100:2:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:400:8100:1:129:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:400:8100:1:0:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        [InlineData("1654:1654:400:8100:1:48:1:2:3:4:extra|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000")]
        public void Wrong_owner_mode_symlink_hardlink_size_and_shape_are_rejected(string metadata) =>
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateMetadata(metadata, false, 128));

        [Fact]
        public void Private_directory_requires_owner_only_mode_and_directory_type()
        {
            ValidatePrivateMetadata("1654:1654:700:41c0:2:4096:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000", true, 0);
            ValidatePrivateMetadata("1654:1654:400:8100:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000", false, 128);
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateMetadata("1654:1654:750:41e8:2:4096:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000", true, 0));
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateMetadata("1654:1654:700:81c0:1:48:1:2:3:4|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000", true, 0));
            ValidateCanonicalExternalPath(Selection().ConfigFile, Selection().ConfigFile + "\n");
            Assert.Throws<InvalidOperationException>(() => ValidateCanonicalExternalPath(Selection().ConfigFile, "/tmp/request.json\n"));
        }


        [Fact]
        public void Pinned_opened_identity_rejects_inode_rename_ABA_and_every_metadata_change()
        {
            const string stamp = "|2026-11-23 12:00:00.000000001 +0000|2026-11-23 12:00:00.000000002 +0000";
            const string identity = "1654:1654:400:8100:1:48:1:2:3:4";
            var admitted = identity + stamp;
            ValidatePrivateFileIdentity(admitted, admitted);
            var fields = identity.Split(':');
            for (var index = 0; index < fields.Length; index++)
            {
                var changed = fields.ToArray();
                changed[index] += "1";
                Assert.Throws<InvalidOperationException>(() => ValidatePrivateFileIdentity(admitted, string.Join(":", changed) + stamp));
            }
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateFileIdentity(admitted,
                admitted.Replace("000000001", "000000003", StringComparison.Ordinal)));
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateFileIdentity(admitted,
                admitted.Replace("000000002", "000000004", StringComparison.Ordinal)));
            // Restoring the pathname's original inode cannot admit a different inode held by the open descriptor.
            var uncheckedOpened = admitted.Replace(":1:2:3:4|", ":1:9:3:4|", StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => ValidatePrivateFileIdentity(admitted, uncheckedOpened));
        }

        [Fact]
        public void Proc_descriptor_path_rejects_invalid_or_unbounded_numeric_handles()
        {
            Assert.Equal("/proc/1654/fd/17", ExternalDescriptorPath(1654, 17));
            foreach (var descriptor in new[] { long.MinValue, -1L, 0L, 1L, 2L, (long)int.MaxValue + 1, long.MaxValue })
                Assert.Throws<InvalidOperationException>(() => ExternalDescriptorPath(1654, descriptor));
            Assert.Throws<InvalidOperationException>(() => ExternalDescriptorPath(0, 17));
        }

        [Fact]
        public void Populated_or_malformed_provider_catalog_is_rejected_before_bootstrap()
        {
            using var schemas = new System.Data.DataTable();
            schemas.Columns.Add("schema_name", typeof(string));
            foreach (var name in new[] { "public", "pg_catalog", "information_schema" }) schemas.Rows.Add(name);
            using var tables = new System.Data.DataTable();
            tables.Columns.Add("table_schema", typeof(string));
            tables.Rows.Add("pg_catalog");
            ValidateEmptyExternalCatalog(schemas, tables);
            tables.Rows.Add("public");
            Assert.Throws<InvalidOperationException>(() => ValidateEmptyExternalCatalog(schemas, tables));
            tables.Rows.Clear();
            schemas.Rows.Add("zeka");
            Assert.Throws<InvalidOperationException>(() => ValidateEmptyExternalCatalog(schemas, tables));
            schemas.Columns.Clear();
            Assert.Throws<InvalidOperationException>(() => ValidateEmptyExternalCatalog(schemas, tables));
        }

        [Fact]
        public void Every_loaded_credential_is_redacted_including_first_error_and_native_output()
        {
            var secrets = new[] { Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") };
            var message = string.Join(" ", secrets);
            FixtureFailure? first = null;
            var captured = CaptureFixtureFailure(ref first, new InvalidOperationException(message), "external-first", secrets[0], secrets[1..]);
            Assert.Same(captured, CaptureFixtureFailure(ref first, new Exception(message), "dispose", secrets[0], secrets[1..]));
            var safe = JsonSerializer.Serialize(new { captured, native = ProtectFixtureText(message, secrets[0], 4096, secrets[1..]) });
            foreach (var secret in secrets) Assert.DoesNotContain(secret, safe);
        }
    }

    [Fact]
    public async Task Unexpected_managed_schema_owner_fails_closed_without_adoption()
    {
        await ExecuteAdministratorAsync("CREATE SCHEMA zeka");
        var originalIdentity = await ExecuteAdministratorScalarAsync<string>("""
            SELECT namespace.oid::text || '|' || pg_catalog.pg_get_userbyid(namespace.nspowner)
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname='zeka'
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql")));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        Assert.Equal("managed schema zeka has unexpected owner", exception.MessageText);
        Assert.Equal(originalIdentity, await ExecuteAdministratorScalarAsync<string>("""
            SELECT namespace.oid::text || '|' || pg_catalog.pg_get_userbyid(namespace.nspowner)
            FROM pg_catalog.pg_namespace namespace
            WHERE namespace.nspname='zeka'
            """));
    }

    [Fact]
    public async Task Fresh_database_migrates_through_owner_assumption_and_matches_auth_manifest()
    {
        Assert.False(await ExecuteAdministratorScalarAsync<bool>(
            "SELECT pg_catalog.to_regnamespace('zeka') IS NOT NULL"));

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        var provisionedSchemaOid = await ExecuteAdministratorScalarAsync<long>(
            "SELECT oid::bigint FROM pg_catalog.pg_namespace WHERE nspname='zeka'");
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactManagedSchemaPrivilegesSql));

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        Assert.Equal(provisionedSchemaOid, await ExecuteAdministratorScalarAsync<long>(
            "SELECT oid::bigint FROM pg_catalog.pg_namespace WHERE nspname='zeka'"));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactManagedSchemaPrivilegesSql));

        await ExecuteAdministratorAsync($"ALTER ROLE zeka_auth_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}';");

        var migratorConnection = Connection("zeka_auth_migrator", MigratorPassword);
        var runtimeConnection = Connection("zeka_auth_runtime", RuntimePassword);
        await AssertInsufficientPrivilegeAsync(migratorConnection, "CREATE SCHEMA issue46_migrator_forbidden");
        await AssertInsufficientPrivilegeAsync(runtimeConnection, "CREATE SCHEMA issue46_runtime_forbidden");
        await AssertInsufficientPrivilegeAsync(migratorConnection,
            "SET ROLE zeka_auth_owner; CREATE SCHEMA issue46_owner_forbidden");
        await AssertInsufficientPrivilegeAsync(migratorConnection,
            "CREATE TABLE zeka.issue46_direct_migrator_forbidden(id integer)");
        await ExecuteAsync(migratorConnection, """
            SET ROLE zeka_auth_owner;
            CREATE TABLE zeka.issue46_owner_assumption_probe(id integer);
            DROP TABLE zeka.issue46_owner_assumption_probe;
            RESET ROLE;
            """);

        await using (var migration = Context(migratorConnection))
        {
            await migration.Database.OpenConnectionAsync();
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            const string predecessor = "20261018144506_BrowserSessionSnapshot";
            const string onboardingTarget = "20261115195218_OnboardingRegistration";
            const string ownerIndexTarget = "20261124170000_OrganisationSingleActiveOwner";
            var migrations = migration.Database.GetMigrations().ToArray();
            Assert.True(migrations.Length > 2);
            Assert.Equal(predecessor, migrations[^3]);
            Assert.Equal(onboardingTarget, migrations[^2]);
            Assert.Equal(ownerIndexTarget, migrations[^1]);
            await migration.GetService<IMigrator>().MigrateAsync(predecessor);
            Assert.Equal([onboardingTarget, ownerIndexTarget], await migration.Database.GetPendingMigrationsAsync());
            // Historical migrations reset the session role. Re-enter the existing owner
            // only at this known boundary before the exact EF target.
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await migration.GetService<IMigrator>().MigrateAsync(onboardingTarget);
            Assert.Equal([ownerIndexTarget], await migration.Database.GetPendingMigrationsAsync());
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await migration.GetService<IMigrator>().MigrateAsync(ownerIndexTarget);
            Assert.Empty(await migration.Database.GetPendingMigrationsAsync());
        }

        await using var verification = Context(migratorConnection);
        // This EF/bootstrap compatibility reference preserves Auth's component contract.
        // The canonical atomic executor and its unmasked v20 evidence belong to DbMigrate tests.
        // Reconcile the actual migrated lifecycle schema before runtime/catalog verification.
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        await new RuntimeDatabaseIdentityValidator(runtimeConnection, "zeka_auth_runtime").StartAsync(default);
        // Optional private evidence is captured before the deliberate drift/unknown objects below.
        await WriteOptionalCatalogSnapshotAsync();

        var maskedAdministrator = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        {
            Options = "-c role=zeka_auth_runtime",
            Pooling = false
        }.ConnectionString;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RuntimeDatabaseIdentityValidator(maskedAdministrator, "zeka_auth_runtime").StartAsync(default));

        await ExecuteAdministratorAsync($"""
            SET ROLE zeka_auth_owner;
            CREATE TABLE public.issue45_unknown_auth(id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY);
            RESET ROLE;
            GRANT ALL ON TABLE public.issue45_unknown_auth TO zeka_auth_runtime;
            GRANT ALL ON SEQUENCE public.issue45_unknown_auth_id_seq TO zeka_auth_runtime;
            GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO zeka_auth_runtime;
            ALTER ROLE zeka_auth_owner IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_migrator IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_runtime IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_closure_recovery IN DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} SET application_name='unexpected';
            ALTER ROLE zeka_auth_closure_recovery LOGIN BYPASSRLS CREATEDB CREATEROLE INHERIT REPLICATION;
            GRANT CREATE, TEMPORARY ON DATABASE {new NpgsqlCommandBuilder().QuoteIdentifier(new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()).Database!)} TO zeka_auth_closure_recovery;
            GRANT CREATE, USAGE ON SCHEMA public, zeka TO zeka_auth_closure_recovery;
            GRANT zeka_auth_closure_recovery TO zeka_auth_runtime WITH ADMIN OPTION;
            GRANT ALL ON TABLE public."AspNetUsers" TO zeka_auth_closure_recovery;
            GRANT ALL ON SEQUENCE public."AspNetUserClaims_Id_seq" TO zeka_auth_closure_recovery;
            """);
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
            SELECT
              (SELECT count(*)=0 FROM pg_catalog.pg_db_role_setting s
               JOIN pg_catalog.pg_roles r ON r.oid=s.setrole
               JOIN pg_catalog.pg_database d ON d.oid=s.setdatabase
               WHERE d.datname=pg_catalog.current_database()
                 AND r.rolname=ANY(ARRAY[
                   'zeka_auth_owner','zeka_auth_migrator','zeka_auth_runtime',
                   'zeka_auth_closure_recovery']))
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','SELECT')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','INSERT')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','UPDATE')
              AND pg_catalog.has_table_privilege('zeka_auth_runtime','public."AspNetUsers"','DELETE')
              AND NOT pg_catalog.has_table_privilege('zeka_auth_runtime','public."__EFMigrationsHistory"','SELECT')
              AND NOT pg_catalog.has_table_privilege('zeka_auth_runtime','public.issue45_unknown_auth','SELECT')
              AND NOT pg_catalog.has_sequence_privilege('zeka_auth_runtime','public.issue45_unknown_auth_id_seq','USAGE')
              AND NOT pg_catalog.has_table_privilege(
                'zeka_auth_closure_recovery','public."AspNetUsers"','SELECT')
              AND NOT pg_catalog.has_sequence_privilege(
                'zeka_auth_closure_recovery','public."AspNetUserClaims_Id_seq"','USAGE')
              AND NOT pg_catalog.pg_has_role(
                'zeka_auth_runtime','zeka_auth_closure_recovery','MEMBER')
            """));
        await ExecuteAdministratorAsync("DROP TABLE public.issue45_unknown_auth");
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
        await AssertInsufficientPrivilegeAsync(runtimeConnection,
            "GRANT zeka_auth_closure_recovery TO zeka_auth_runtime");

        await ExecuteAdministratorAsync("""
            CREATE FUNCTION zeka.issue46_unknown_function() RETURNS integer
              LANGUAGE sql SECURITY INVOKER AS 'SELECT 1';
            CREATE FUNCTION public.issue46_unknown_function() RETURNS integer
              LANGUAGE sql SECURITY INVOKER AS 'SELECT 1';
            CREATE TABLE public.issue46_unknown_lifecycle (id uuid PRIMARY KEY);
            CREATE TABLE zeka.issue46_unknown_lifecycle (id uuid PRIMARY KEY);
            GRANT CREATE, USAGE ON SCHEMA zeka TO zeka_auth_runtime;
            GRANT ALL ON ALL TABLES IN SCHEMA public, zeka TO zeka_auth_runtime;
            GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA public, zeka TO zeka_auth_runtime;
            GRANT SELECT ON TABLE public."AuthClosureParticipantInbox" TO zeka_auth_migrator;
            GRANT UPDATE ("PayloadSha256") ON TABLE public."AuthClosureParticipantInbox" TO zeka_auth_migrator;
            GRANT EXECUTE ON FUNCTION zeka.reject_auth_owned_write_during_closure_fence()
              TO zeka_auth_migrator;
            """);

        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));

        await RecordPrivilegeDifferencesAsync("table", ExactLifecycleTablePrivilegesSql);
        await RecordPrivilegeDifferencesAsync("column", ExactLifecycleColumnPrivilegesSql);
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactLifecycleTablePrivilegesSql));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>(ExactLifecycleColumnPrivilegesSql));
        Assert.True(await ExecuteAdministratorScalarAsync<bool>("""
            SELECT
              pg_catalog.has_schema_privilege('zeka_auth_runtime', 'public', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'public', 'CREATE')
              AND pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'USAGE')
              AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'CREATE')
              AND (
                SELECT pg_catalog.array_agg(n.nspname || '.' || p.proname ORDER BY n.nspname, p.proname)
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(ARRAY['public','zeka'])
                  AND pg_catalog.has_function_privilege('zeka_auth_runtime', p.oid, 'EXECUTE')
              ) = ARRAY['zeka.current_organisation_id',
                        'zeka.reject_auth_membership_write_during_export_fence',
                        'zeka.reject_auth_organisation_write_during_closure_fence',
                        'zeka.reject_auth_owned_write_during_closure_fence']
              AND NOT pg_catalog.has_function_privilege(
                'zeka_auth_runtime',
                'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
              AND pg_catalog.has_function_privilege(
                'zeka_auth_closure_recovery',
                'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
              AND (
                SELECT pg_catalog.array_agg(n.nspname || '.' || p.proname ORDER BY n.nspname, p.proname)
                FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname=ANY(ARRAY['public','zeka'])
                  AND pg_catalog.has_function_privilege(
                    'zeka_auth_closure_recovery', p.oid, 'EXECUTE')
              ) = ARRAY['zeka.release_auth_closure_fence']
              AND NOT EXISTS (
                SELECT FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(p.proacl) acl
                LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
                WHERE n.nspname='zeka'
                  AND p.proname=ANY(ARRAY[
                    'reject_auth_membership_write_during_export_fence',
                    'reject_auth_organisation_write_during_closure_fence',
                    'reject_auth_owned_write_during_closure_fence',
                    'release_auth_closure_fence'])
                  AND acl.grantee<>p.proowner
                  AND (grantee.rolname IS DISTINCT FROM CASE
                        WHEN p.proname='release_auth_closure_fence'
                          THEN 'zeka_auth_closure_recovery'
                        ELSE 'zeka_auth_runtime'
                      END
                    OR acl.privilege_type<>'EXECUTE'
                    OR acl.is_grantable)
              )
              AND NOT EXISTS (
                SELECT FROM pg_catalog.pg_proc p
                JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='zeka'
                  AND p.proname=ANY(ARRAY[
                    'reject_auth_membership_write_during_export_fence',
                    'reject_auth_organisation_write_during_closure_fence',
                    'reject_auth_owned_write_during_closure_fence',
                    'release_auth_closure_fence'])
                  AND pg_catalog.pg_get_userbyid(p.proowner)<>'zeka_auth_owner'
              )
            """));

        await ExecuteAdministratorAsync("""
            DROP TABLE public.issue46_unknown_lifecycle,
              zeka.issue46_unknown_lifecycle;
            DROP FUNCTION public.issue46_unknown_function();
            DROP FUNCTION zeka.issue46_unknown_function();
            """);
        await ExecuteAdministratorAsync(ReadBootstrapScript("bootstrap-auth-roles.sql"));
        await RlsSecurityManifestVerifier.VerifyAsync(verification, typeof(AuthDbContext).Assembly);
    }

    private async Task WriteOptionalCatalogSnapshotAsync()
    {
        var destination = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_CATALOG_SNAPSHOT_FILE");
        if (destination is null) return;
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_LINUX_REQUIRED");
        try
        {
            if (!Path.IsPathFullyQualified(destination) || Path.GetFullPath(destination) != destination
                || Path.GetExtension(destination) != ".json")
                throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_PATH_REJECTED");
            var parent = Path.GetDirectoryName(destination);
            if (parent is null || !Directory.Exists(parent) || Path.Exists(destination))
                throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_PATH_REJECTED");
            for (DirectoryInfo? directory = new(parent); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_PATH_REJECTED");
            var directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            var fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (File.GetUnixFileMode(parent) != directoryMode)
                throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_MODE_REJECTED");
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = budget.Token;
            await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("""
            WITH namespaces AS (
              SELECT * FROM pg_catalog.pg_namespace WHERE nspname IN ('public','zeka')
            ), relations AS (
              SELECT c.*,n.nspname FROM pg_catalog.pg_class c JOIN namespaces n ON n.oid=c.relnamespace
            ), managed_roles AS (
              SELECT * FROM pg_catalog.pg_roles WHERE left(rolname::text,10)='zeka_auth_'
            ), state AS (
              SELECT pg_catalog.jsonb_build_array('database','<current>',pg_catalog.pg_get_userbyid(d.datdba),
                pg_catalog.pg_encoding_to_char(d.encoding),d.datcollate,d.datctype,d.datallowconn,d.datconnlimit) AS value
                FROM pg_catalog.pg_database d WHERE d.datname=pg_catalog.current_database()
              UNION ALL SELECT pg_catalog.jsonb_build_array('database-acl','<current>',
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM pg_catalog.pg_database d CROSS JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(d.datacl,pg_catalog.acldefault('d',d.datdba))) a
                WHERE d.datname=pg_catalog.current_database()
              UNION ALL SELECT pg_catalog.jsonb_build_array('schema',n.nspname,
                pg_catalog.pg_get_userbyid(n.nspowner),n.nspacl IS NOT NULL) FROM namespaces n
              UNION ALL SELECT pg_catalog.jsonb_build_array('schema-acl',n.nspname,
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM namespaces n CROSS JOIN LATERAL pg_catalog.aclexplode(
                  COALESCE(n.nspacl,pg_catalog.acldefault('n',n.nspowner))) a
              UNION ALL SELECT pg_catalog.jsonb_build_array('relation',c.nspname,c.relname,c.relkind,
                pg_catalog.pg_get_userbyid(c.relowner),c.relpersistence,c.relrowsecurity,c.relforcerowsecurity,
                c.relispartition,c.relacl IS NOT NULL,
                (SELECT pg_catalog.jsonb_agg(option ORDER BY option) FROM unnest(c.reloptions) option)) FROM relations c
              UNION ALL SELECT pg_catalog.jsonb_build_array('relation-acl',c.nspname,c.relname,
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM relations c CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(c.relacl,
                  pg_catalog.acldefault(CASE WHEN c.relkind='S' THEN 'S'::"char" ELSE 'r'::"char" END,c.relowner))) a
                WHERE c.relkind IN ('r','p','v','m','f','S')
              UNION ALL SELECT pg_catalog.jsonb_build_array('column',c.nspname,c.relname,a.attnum,a.attname,
                pg_catalog.format_type(a.atttypid,a.atttypmod),a.attnotnull,a.attidentity,a.attgenerated,
                a.attisdropped,a.atthasmissing,a.attacl IS NOT NULL,cn.nspname,coll.collname,
                pg_catalog.pg_get_expr(d.adbin,d.adrelid,false))
                FROM relations c JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid
                LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
                LEFT JOIN pg_catalog.pg_collation coll ON coll.oid=a.attcollation
                LEFT JOIN pg_catalog.pg_namespace cn ON cn.oid=coll.collnamespace WHERE a.attnum>0
              UNION ALL SELECT pg_catalog.jsonb_build_array('column-acl',c.nspname,c.relname,col.attname,
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM relations c JOIN pg_catalog.pg_attribute col ON col.attrelid=c.oid
                CROSS JOIN LATERAL pg_catalog.aclexplode(col.attacl) a WHERE col.attnum>0
              UNION ALL SELECT pg_catalog.jsonb_build_array('constraint',c.nspname,c.relname,t.conname,
                t.contype,t.condeferrable,t.condeferred,t.convalidated,pg_catalog.pg_get_constraintdef(t.oid,false))
                FROM relations c JOIN pg_catalog.pg_constraint t ON t.conrelid=c.oid
              UNION ALL SELECT pg_catalog.jsonb_build_array('index',c.nspname,c.relname,ic.relname,
                i.indisunique,i.indisprimary,i.indisexclusion,i.indimmediate,i.indnullsnotdistinct,
                i.indisvalid,i.indisready,i.indislive,pg_catalog.pg_get_indexdef(i.indexrelid,0,false))
                FROM relations c JOIN pg_catalog.pg_index i ON i.indrelid=c.oid
                JOIN pg_catalog.pg_class ic ON ic.oid=i.indexrelid
              UNION ALL SELECT pg_catalog.jsonb_build_array('sequence',c.nspname,c.relname,
                pg_catalog.format_type(s.seqtypid,NULL),s.seqstart,s.seqincrement,s.seqmax,s.seqmin,s.seqcache,s.seqcycle)
                FROM relations c JOIN pg_catalog.pg_sequence s ON s.seqrelid=c.oid
              UNION ALL SELECT pg_catalog.jsonb_build_array('sequence-owned-by',c.nspname,c.relname,
                rn.nspname,ref.relname,a.attname,d.deptype)
                FROM relations c JOIN pg_catalog.pg_depend d ON d.classid='pg_catalog.pg_class'::pg_catalog.regclass
                  AND d.objid=c.oid AND d.refclassid='pg_catalog.pg_class'::pg_catalog.regclass AND d.deptype IN ('a','i')
                JOIN pg_catalog.pg_class ref ON ref.oid=d.refobjid JOIN pg_catalog.pg_namespace rn ON rn.oid=ref.relnamespace
                LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=ref.oid AND a.attnum=d.refobjsubid WHERE c.relkind='S'
              UNION ALL SELECT pg_catalog.jsonb_build_array('function',n.nspname,p.proname,
                pg_catalog.pg_get_function_identity_arguments(p.oid),p.prokind,pg_catalog.pg_get_userbyid(p.proowner),
                p.prosecdef,p.proleakproof,p.proisstrict,p.provolatile,p.proparallel,p.proacl IS NOT NULL,
                (SELECT pg_catalog.jsonb_agg(setting ORDER BY setting) FROM unnest(p.proconfig) setting),
                CASE WHEN p.prokind='a' THEN pg_catalog.pg_get_function_arguments(p.oid) ELSE pg_catalog.pg_get_functiondef(p.oid) END)
                FROM pg_catalog.pg_proc p JOIN namespaces n ON n.oid=p.pronamespace
              UNION ALL SELECT pg_catalog.jsonb_build_array('function-acl',n.nspname,p.proname,
                pg_catalog.pg_get_function_identity_arguments(p.oid),
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM pg_catalog.pg_proc p JOIN namespaces n ON n.oid=p.pronamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(p.proacl,pg_catalog.acldefault('f',p.proowner))) a
              UNION ALL SELECT pg_catalog.jsonb_build_array('trigger',c.nspname,c.relname,t.tgisinternal,
                CASE WHEN t.tgisinternal THEN con.conname::text ELSE t.tgname::text END,pn.nspname,p.proname,
                t.tgtype,t.tgenabled,t.tgdeferrable,t.tginitdeferred,pg_catalog.encode(t.tgargs,'hex'),
                pg_catalog.pg_get_expr(t.tgqual,t.tgrelid,false),
                CASE WHEN t.tgisinternal THEN NULL ELSE pg_catalog.pg_get_triggerdef(t.oid,false) END)
                FROM relations c JOIN pg_catalog.pg_trigger t ON t.tgrelid=c.oid
                JOIN pg_catalog.pg_proc p ON p.oid=t.tgfoid JOIN pg_catalog.pg_namespace pn ON pn.oid=p.pronamespace
                LEFT JOIN pg_catalog.pg_constraint con ON con.oid=t.tgconstraint
              UNION ALL SELECT pg_catalog.jsonb_build_array('type',n.nspname,t.typname,t.typtype,
                pg_catalog.pg_get_userbyid(t.typowner),t.typnotnull,pg_catalog.format_type(t.typbasetype,t.typtypmod),
                pg_catalog.pg_get_expr(t.typdefaultbin,0,false),t.typacl IS NOT NULL,
                (SELECT pg_catalog.jsonb_agg(e.enumlabel ORDER BY e.enumsortorder) FROM pg_catalog.pg_enum e WHERE e.enumtypid=t.oid))
                FROM pg_catalog.pg_type t JOIN namespaces n ON n.oid=t.typnamespace
              UNION ALL SELECT pg_catalog.jsonb_build_array('type-acl',n.nspname,t.typname,
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM pg_catalog.pg_type t JOIN namespaces n ON n.oid=t.typnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(COALESCE(t.typacl,pg_catalog.acldefault('T',t.typowner))) a
              UNION ALL SELECT pg_catalog.jsonb_build_array('domain-constraint',n.nspname,t.typname,c.conname,
                c.convalidated,pg_catalog.pg_get_constraintdef(c.oid,false))
                FROM pg_catalog.pg_type t JOIN namespaces n ON n.oid=t.typnamespace
                JOIN pg_catalog.pg_constraint c ON c.contypid=t.oid
              UNION ALL SELECT pg_catalog.jsonb_build_array('policy',c.nspname,c.relname,p.polname,p.polcmd,p.polpermissive,
                (SELECT pg_catalog.jsonb_agg(role_name ORDER BY role_name) FROM
                  (SELECT CASE WHEN role_oid=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(role_oid)::text END role_name
                   FROM unnest(p.polroles) role_oid) roles),
                pg_catalog.pg_get_expr(p.polqual,p.polrelid,false),pg_catalog.pg_get_expr(p.polwithcheck,p.polrelid,false))
                FROM relations c JOIN pg_catalog.pg_policy p ON p.polrelid=c.oid
              UNION ALL SELECT pg_catalog.jsonb_build_array('role',r.rolname,r.rolsuper,r.rolinherit,r.rolcreaterole,
                r.rolcreatedb,r.rolcanlogin,r.rolreplication,r.rolbypassrls,r.rolconnlimit,r.rolvaliduntil::text)
                FROM managed_roles r
              UNION ALL SELECT pg_catalog.jsonb_build_array('membership',parent.rolname,member.rolname,
                pg_catalog.pg_get_userbyid(m.grantor),m.admin_option,m.inherit_option,m.set_option)
                FROM pg_catalog.pg_auth_members m JOIN pg_catalog.pg_roles parent ON parent.oid=m.roleid
                JOIN pg_catalog.pg_roles member ON member.oid=m.member
                WHERE m.roleid IN (SELECT oid FROM managed_roles) OR m.member IN (SELECT oid FROM managed_roles)
              UNION ALL SELECT pg_catalog.jsonb_build_array('role-setting',
                CASE WHEN s.setrole=0 THEN '<all-roles>' ELSE pg_catalog.pg_get_userbyid(s.setrole)::text END,
                CASE WHEN s.setdatabase=0 THEN '<global>' ELSE '<current>' END,setting)
                FROM pg_catalog.pg_db_role_setting s CROSS JOIN LATERAL unnest(s.setconfig) setting
                WHERE (s.setrole=0 OR s.setrole IN (SELECT oid FROM managed_roles))
                  AND (s.setdatabase=0 OR s.setdatabase=(SELECT oid FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()))
              UNION ALL SELECT pg_catalog.jsonb_build_array('default-acl-scope',owner.rolname,
                COALESCE(n.nspname::text,'<global>'),d.defaclobjtype)
                FROM pg_catalog.pg_default_acl d JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole
                LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
              UNION ALL SELECT pg_catalog.jsonb_build_array('default-acl',owner.rolname,
                COALESCE(n.nspname::text,'<global>'),d.defaclobjtype,
                CASE WHEN a.grantee=0 THEN 'PUBLIC' ELSE pg_catalog.pg_get_userbyid(a.grantee)::text END,
                pg_catalog.pg_get_userbyid(a.grantor),a.privilege_type,a.is_grantable)
                FROM pg_catalog.pg_default_acl d JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole
                LEFT JOIN pg_catalog.pg_namespace n ON n.oid=d.defaclnamespace
                CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) a
              UNION ALL SELECT pg_catalog.jsonb_build_array('history',"MigrationId","ProductVersion") FROM public."__EFMigrationsHistory"
            ) SELECT value::text FROM state
            """, connection) { CommandTimeout = 30 };
            command.CommandText = command.CommandText.Replace("\r\n", "\n", StringComparison.Ordinal);
            var queryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText))).ToLowerInvariant();
            if (queryHash != "7c42bf41bc2649c3447d55146e214f89713f56ecbd95389b9472d06801f62ae0")
                throw new InvalidOperationException("AUTH_CATALOG_QUERY_IDENTITY_REJECTED");
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<string>();
            while (await reader.ReadAsync(token))
            {
                var row = reader.GetString(0);
                if (rows.Count >= 20000 || row.Length > 65536)
                    throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_CAPTURE_REJECTED");
                rows.Add(row);
            }
            // Keep all metadata values, including raw migration ProductVersion, unchanged.
            // Assembly identities are observed here; no prior run/source identity is invented.
            var snapshot = new
            {
                SchemaVersion = "zeka-auth-catalog/v1", QuerySha256 = queryHash,
                Rows = rows.OrderBy(row => row, StringComparer.Ordinal).ToArray(),
                EntityFramework = CatalogAssemblyIdentity.Read(typeof(DbContext).Assembly),
                AuthInfrastructure = CatalogAssemblyIdentity.Read(typeof(AuthDbContext).Assembly),
                ProducerAssembly = CatalogAssemblyIdentity.Read(typeof(PostgreSqlAuthRoleCatalogEvidenceTests).Assembly),
                CapturedAtUtc = DateTimeOffset.UtcNow
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            await using var file = new FileStream(destination, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = fileMode
            });
            if (File.GetUnixFileMode(destination) != fileMode)
                throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_MODE_REJECTED");
            await file.WriteAsync(bytes, token);
            await file.FlushAsync(token);
            file.Flush(flushToDisk: true);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith("AUTH_CATALOG_", StringComparison.Ordinal))
        {
            throw;
        }
        catch
        {
            // Metadata capture must not publish provider details, paths or connection material.
            throw new InvalidOperationException("AUTH_CATALOG_SNAPSHOT_CAPTURE_REJECTED");
        }
    }

    private sealed record CatalogAssemblyIdentity(string Name, string Version, string InformationalVersion,
        string ModuleVersionId, string Sha256)
    {
        internal static CatalogAssemblyIdentity Read(Assembly assembly) => new(
            assembly.GetName().Name ?? throw new InvalidOperationException("AUTH_CATALOG_ASSEMBLY_IDENTITY_REQUIRED"),
            assembly.GetName().Version?.ToString() ?? string.Empty,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
            assembly.ManifestModule.ModuleVersionId.ToString("D"),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant());
    }

    private async Task RecordPrivilegeDifferencesAsync(string inventory, string exactSql)
    {
        var inventories = exactSql[..exactSql.IndexOf("SELECT NOT EXISTS", StringComparison.Ordinal)];
        var differences = await ExecuteAdministratorScalarAsync<string>(inventories + """
            SELECT jsonb_build_object(
              'actual_minus_expected', (SELECT coalesce(jsonb_agg(to_jsonb(delta)), '[]'::jsonb)
                FROM (SELECT * FROM actual EXCEPT SELECT * FROM expected) delta),
              'expected_minus_actual', (SELECT coalesce(jsonb_agg(to_jsonb(delta)), '[]'::jsonb)
                FROM (SELECT * FROM expected EXCEPT SELECT * FROM actual) delta))::text
            """);
        output.WriteLine($"{inventory} privilege differences: {differences}");
    }

    private static AuthDbContext Context(string connectionString) => new(
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connectionString).Options);

    private string Connection(string username, string password) =>
        new NpgsqlConnectionStringBuilder(postgres.GetConnectionString())
        { Username = username, Password = password }.ConnectionString;

    private async Task ExecuteAdministratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<T> ExecuteAdministratorScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertInsufficientPrivilegeAsync(string connectionString, string sql)
    {
        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static string ReadBootstrapScript(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed database role bootstrap script was not found.", fileName);
    }

    private const string ExactLifecycleTablePrivilegesSql = """
        WITH expected(table_name, grantee, privilege, is_grantable) AS (
          SELECT table_name, 'zeka_auth_runtime', privilege, false
          FROM unnest(ARRAY[
            'AspNetRoleClaims','AspNetRoles','AspNetUserClaims','AspNetUserLogins','AspNetUserRoles',
            'AspNetUserTokens','AspNetUsers','AuditEntries','IdempotencyRecords','OrganisationMemberships',
            'OutboxMessages','PermissionSets'
          ]) table_name
          CROSS JOIN unnest(ARRAY['SELECT','INSERT','UPDATE','DELETE']) privilege
          UNION ALL SELECT 'Organisations', 'zeka_auth_runtime', privilege, false
          FROM unnest(ARRAY['SELECT','DELETE']) privilege
          UNION ALL VALUES
            ('BrowserSessionFamilies','zeka_auth_runtime','SELECT',false),
            ('BrowserSessionFamilies','zeka_auth_runtime','INSERT',false),
            ('BrowserRefreshCredentials','zeka_auth_runtime','SELECT',false),
            ('BrowserRefreshCredentials','zeka_auth_runtime','INSERT',false),
            ('EmailVerificationChallenges','zeka_auth_runtime','SELECT',false),
            ('EmailVerificationChallenges','zeka_auth_runtime','INSERT',false),
            ('VerificationMailOutboxMessages','zeka_auth_runtime','SELECT',false),
            ('VerificationMailOutboxMessages','zeka_auth_runtime','INSERT',false),
            ('OnboardingGrants','zeka_auth_runtime','SELECT',false),
            ('OnboardingGrants','zeka_auth_runtime','INSERT',false),
            ('OnboardingOperationReceipts','zeka_auth_runtime','SELECT',false),
            ('OnboardingOperationReceipts','zeka_auth_runtime','INSERT',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','SELECT',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','INSERT',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','UPDATE',false),
            ('__EFMigrationsHistory','zeka_auth_migrator','DELETE',false),
            ('LifecycleParticipantRegistryRevisions','zeka_auth_runtime','SELECT',false),
            ('LifecycleParticipantRegistryBindings','zeka_auth_runtime','SELECT',false),
            ('LifecycleParticipantRegistryActivation','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantExecutions','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantExecutions','zeka_auth_runtime','INSERT',false),
            ('AuthExportParticipantInbox','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantInbox','zeka_auth_runtime','INSERT',false),
            ('AuthExportParticipantOutbox','zeka_auth_runtime','SELECT',false),
            ('AuthExportParticipantOutbox','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantExecutions','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantExecutions','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantInbox','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantInbox','zeka_auth_runtime','INSERT',false),
            ('AuthClosureParticipantOutbox','zeka_auth_runtime','SELECT',false),
            ('AuthClosureParticipantOutbox','zeka_auth_runtime','INSERT',false),
            ('OrganisationLifecycleOperations','zeka_auth_runtime','SELECT',false),
            ('OrganisationLifecycleOperations','zeka_auth_runtime','INSERT',false),
            ('OrganisationLifecycleParticipants','zeka_auth_runtime','SELECT',false),
            ('OrganisationLifecycleParticipants','zeka_auth_runtime','INSERT',false),
            ('MembershipPermissionGrants','zeka_auth_runtime','SELECT',false),
            ('MembershipPermissionGrants','zeka_auth_runtime','INSERT',false),
            ('LifecycleCoordinatorLeases','zeka_auth_runtime','SELECT',false),
            ('LifecycleCoordinatorLeases','zeka_auth_runtime','INSERT',false),
            ('LifecycleClosureFenceReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleClosureFenceReceipts','zeka_auth_runtime','INSERT',false),
            ('RetentionDecisionSets','zeka_auth_runtime','SELECT',false),
            ('RetentionDecisionSets','zeka_auth_runtime','INSERT',false),
            ('RetentionDecisionRecords','zeka_auth_runtime','SELECT',false),
            ('RetentionDecisionRecords','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportFenceReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportFenceReceipts','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportFragments','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportFragments','zeka_auth_runtime','INSERT',false),
            ('LifecycleExportPackages','zeka_auth_runtime','SELECT',false),
            ('LifecycleExportPackages','zeka_auth_runtime','INSERT',false),
            ('LifecycleInboxReceipts','zeka_auth_runtime','SELECT',false),
            ('LifecycleInboxReceipts','zeka_auth_runtime','INSERT',false)
        ), actual(table_name, grantee, privilege, is_grantable) AS (
          SELECT c.relname, COALESCE(grantee.rolname, 'PUBLIC'), acl.privilege_type, acl.is_grantable
          FROM pg_catalog.pg_class c
          JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(c.relacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE n.nspname=ANY(ARRAY['public','zeka'])
            AND c.relkind IN ('r','p')
            AND acl.grantee<>c.relowner
        )
        SELECT NOT EXISTS (SELECT * FROM actual EXCEPT SELECT * FROM expected)
          AND NOT EXISTS (SELECT * FROM expected EXCEPT SELECT * FROM actual)
          AND NOT EXISTS (
            SELECT FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname=ANY(ARRAY['public','zeka'])
              AND c.relkind IN ('r','p')
              AND pg_catalog.pg_get_userbyid(c.relowner)<>'zeka_auth_owner')
        """;

    private const string ExactLifecycleColumnPrivilegesSql = """
        WITH expected(table_name, column_name, grantee, privilege, is_grantable) AS (
          SELECT table_name, column_name, 'zeka_auth_runtime', 'UPDATE', false
          FROM (VALUES
            ('BrowserSessionFamilies', ARRAY['IdleExpiresAtUtc','RevokedAtUtc']::text[]),
            ('BrowserRefreshCredentials', ARRAY['ConsumedAtUtc']::text[]),
            ('EmailVerificationChallenges', ARRAY['Status','TransitionedAtUtc','Version']::text[]),
            ('VerificationMailOutboxMessages', ARRAY['Status','LeaseId','LeaseExpiresAtUtc','Attempts',
              'NextAttemptAtUtc','DeliveredAtUtc','ObsoleteAtAcceptance','Outcome','Version']::text[]),
            ('OrganisationLifecycleOperations', ARRAY['State','Revision','SnapshotAt','FenceEvidenceHash',
              'PackageSha256','PackageReference','FailureCode','CompletedAt','IsActive','ClosingAt',
              'ArchivedAt','ClosureFenceEvidenceHash','DispositionReadyAt','RetentionDecisionSetHash']::text[]),
            ('OrganisationLifecycleParticipants', ARRAY['State','FailureBoundaryDisposition','FailureCode','FailureRetryable','FailedAt']::text[]),
            ('LifecycleCoordinatorLeases', ARRAY['LeaseId','ExpiresAt','Version']::text[]),
            ('AuthExportParticipantExecutions', ARRAY['SnapshotAt','FenceEvidenceHash','FragmentHash',
              'CategoriesJson','ReleasedAt','State']::text[]),
            ('AuthClosureParticipantExecutions', ARRAY[]::text[]),
            ('MembershipPermissionGrants', ARRAY['RevokedByMembershipId','RevokedBySubjectId',
              'RevokedAtUtc','ConcurrencyVersion']::text[])
          ) inventory(table_name, update_columns)
          CROSS JOIN LATERAL pg_catalog.unnest(inventory.update_columns) column_name
          UNION ALL
          SELECT 'Organisations', column_name, 'zeka_auth_runtime', privilege, false
          FROM pg_catalog.unnest(ARRAY['Id','DisplayName','OwnerUserId','Status','Slug',
            'CreatedAtUtc','UpdatedAtUtc','ConcurrencyVersion']) column_name
          CROSS JOIN pg_catalog.unnest(ARRAY['INSERT','UPDATE']) privilege
        ), actual(table_name, column_name, grantee, privilege, is_grantable) AS (
          SELECT relation.relname, attribute.attname, COALESCE(grantee.rolname, 'PUBLIC'),
            acl.privilege_type, acl.is_grantable
          FROM pg_catalog.pg_attribute attribute
          JOIN pg_catalog.pg_class relation ON relation.oid=attribute.attrelid
          JOIN pg_catalog.pg_namespace namespace ON namespace.oid=relation.relnamespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(attribute.attacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE namespace.nspname=ANY(ARRAY['public','zeka'])
            AND relation.relkind IN ('r','p')
            AND attribute.attnum>0 AND NOT attribute.attisdropped
            AND acl.grantee<>relation.relowner
        )
        SELECT NOT EXISTS (SELECT * FROM actual EXCEPT SELECT * FROM expected)
          AND NOT EXISTS (SELECT * FROM expected EXCEPT SELECT * FROM actual)
        """;

    private const string ExactManagedSchemaPrivilegesSql = """
        WITH schema_acl AS (
          SELECT COALESCE(grantee.rolname, 'PUBLIC') grantee,
            acl.privilege_type,
            acl.is_grantable
          FROM pg_catalog.pg_namespace namespace
          CROSS JOIN LATERAL pg_catalog.aclexplode(namespace.nspacl) acl
          LEFT JOIN pg_catalog.pg_roles grantee ON grantee.oid=acl.grantee
          WHERE namespace.nspname='zeka'
            AND acl.grantee<>namespace.nspowner
        )
        SELECT
          (SELECT pg_catalog.pg_get_userbyid(namespace.nspowner)='zeka_auth_owner'
             FROM pg_catalog.pg_namespace namespace WHERE namespace.nspname='zeka')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_owner', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_migrator', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege('zeka_auth_runtime', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_database_privilege(
            'zeka_auth_closure_recovery', pg_catalog.current_database(), 'CREATE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_migrator', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_migrator', 'zeka', 'CREATE')
          AND pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_runtime', 'zeka', 'CREATE')
          AND pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'zeka', 'USAGE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'zeka', 'CREATE')
          AND NOT pg_catalog.has_schema_privilege('zeka_auth_closure_recovery', 'public', 'USAGE')
          AND (SELECT pg_catalog.count(*)=2 FROM schema_acl)
          AND EXISTS (
            SELECT FROM schema_acl
            WHERE grantee='zeka_auth_runtime'
              AND privilege_type='USAGE'
              AND NOT is_grantable)
          AND EXISTS (
            SELECT FROM schema_acl
            WHERE grantee='zeka_auth_closure_recovery'
              AND privilege_type='USAGE'
              AND NOT is_grantable)
        """;
}
