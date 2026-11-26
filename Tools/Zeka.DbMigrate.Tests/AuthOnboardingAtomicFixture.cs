extern alias DbMigrator;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthManager.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using DotNet.Testcontainers.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Zeka.LocalStack.DatabaseSetup;
using Xunit;
using Xunit.Abstractions;
using SafePostgresDiagnosticCapture = DbMigrator::Zeka.ToolingDiagnostics.SafePostgresDiagnosticCapture;

namespace Zeka.DbMigrate.Tests;

// A single wholly owned cluster, with a fresh Auth database per serial scenario.
// No retained fixture/configuration is accepted. Secret bytes enter only a 0400
// copied container file; it is not described as a read-only bind mount.
public sealed class AuthOnboardingAtomicFixture : IAsyncLifetime, IAuthMigrationDatabaseHost
{
    private const string SecretPath = "/run/secrets/admin-password";
    private const string AuthCatalogDockerEndpoint = "npipe://./pipe/docker_engine";
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string adminSecret = FreshSecret();
    private readonly string migratorSecret = FreshSecret();
    private readonly string runtimeSecret = FreshSecret();
    private INetwork? network;
    private IVolume? volume;
    private IContainer? container;
    private readonly List<IContainer> setupHelpers = [];
    private readonly Dictionary<string, Dictionary<string, byte[]>> setupInputHashes = [];
    private readonly HashSet<string> authCatalogHelpers = [];
    private AuthCatalogRunnerBinding? admittedAuthCatalogBinding;
    private AuthCatalogPostgresBinding? admittedAuthCatalogPostgresBinding;
    private string? observedAuthCatalogPostgresImage;
    internal string? CatalogPostgresImage => observedAuthCatalogPostgresImage;
    internal bool AuthCatalogTransportRequested { get; init; }
    internal string CatalogRunId => id;
    private bool failed;
    private ushort port;
    private string project = string.Empty;
    private string stage = "fixture-construction";

    public async Task InitializeAsync()
    {
        if (AuthCatalogTransportRequested)
        {
            RequireAuthCatalogLocalEngine();
            if (admittedAuthCatalogBinding is null || admittedAuthCatalogPostgresBinding is null)
                throw new InvalidOperationException("AUTH_CATALOG_IMAGE_ADMISSION_REQUIRED_BEFORE_RESOURCE_CREATION");
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = deadline.Token;
        try
        {
            project = "zeka-auth-atomic-tests-" + id;
            var labels = new Dictionary<string, string>
            {
                ["com.docker.compose.project"] = project,
                ["com.diliwo.zeka.fixture-id"] = id,
                ["com.diliwo.zeka.scope"] = "auth-onboarding-atomic-provider-tests",
                ["com.diliwo.zeka.owner"] = "codex"
            };
            stage = "owned-network-volume";
            // Docker29 skips host port mapping on internal-only networks. Native Windows
            // tests use a fresh dedicated bridge, strict loopback-only publication, and
            // actual membership/port readback below; no unrelated network is reused.
            var networkBuilder = new NetworkBuilder().WithName(project + "-private").WithLabel(labels)
                .WithCleanUp(false).WithCreateParameterModifier(value => value.Internal = false);
            if (AuthCatalogTransportRequested) networkBuilder = networkBuilder.WithDockerEndpoint(AuthCatalogDockerEndpoint);
            network = networkBuilder.Build();
            await network.CreateAsync(ct);
            var volumeBuilder = new VolumeBuilder().WithName(project + "-data").WithLabel(labels).WithCleanUp(false);
            if (AuthCatalogTransportRequested) volumeBuilder = volumeBuilder.WithDockerEndpoint(AuthCatalogDockerEndpoint);
            volume = volumeBuilder.Build();
            await volume.CreateAsync(ct);
            var bytes = Encoding.UTF8.GetBytes(adminSecret);
            try
            {
                // Generic builder preserves PostgreSQL durability defaults. The PG module
                // adds password environment values and disables fsync/synchronous_commit.
                var postgresBuilder = new ContainerBuilder().WithImage(AuthCatalogTransportRequested
                        ? admittedAuthCatalogPostgresBinding!.Image : "postgres:17-alpine").WithName(project + "-postgres")
                    .WithLabel(labels).WithNetwork(network).WithNetworkAliases(AuthCatalogTransportRequested ? new[] { "postgres", "postgres-" + id } : new[] { "postgres" }).WithCleanUp(false).WithAutoRemove(false)
                    .WithEnvironment("POSTGRES_USER", "postgres")
                    .WithEnvironment("POSTGRES_PASSWORD_FILE", SecretPath)
                    .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256")
                    .WithResourceMapping(bytes, SecretPath, uid: 70, gid: 70, fileMode: UnixFileModes.UserRead)
                    .WithVolumeMount(volume, "/var/lib/postgresql/data", AccessMode.ReadWrite)
                    .WithPortBinding(5432, true)
                    .WithCreateParameterModifier(parameters =>
                    {
                        if (parameters.HostConfig.PortBindings.Count != 1)
                            throw new InvalidOperationException("FIXTURE_PORT_BOUNDARY_REJECTED");
                        foreach (var bindings in parameters.HostConfig.PortBindings.Values)
                        foreach (var binding in bindings) binding.HostIP = "127.0.0.1";
                    })
                    .WithCommand("-c", "log_statement=none", "-c", "log_min_error_statement=panic")
                    .WithLogger(NullLogger.Instance)
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                        ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres"]));
                if (AuthCatalogTransportRequested) postgresBuilder = postgresBuilder
                    .WithDockerEndpoint(AuthCatalogDockerEndpoint).WithImagePullPolicy(_ => false);
                container = postgresBuilder.Build();
                stage = "owned-container-start";
                await container.StartAsync(ct);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            port = container.GetMappedPublicPort(5432);
            stage = "actual-docker-resource-admission";
            await InspectAsync(ct);
            var mode = await container.ExecAsync(["stat", "-c", "%u:%g:%a", SecretPath], ct);
            Assert.Equal(0, mode.ExitCode);
            Assert.Equal("70:70:400", mode.Stdout.Trim());
            stage = "fresh-postgresql-and-durability-admission";
            await using var connection = await OpenAdminAsync("postgres", ct);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('server_version_num')::integer/10000=17
                  AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on'
                  AND current_setting('synchronous_commit')='on'
                  AND session_user='postgres' AND current_user='postgres' AND current_database()='postgres'
                  AND current_setting('log_statement')='none' AND current_setting('log_min_error_statement')='panic'
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_database WHERE datname NOT IN ('postgres','template0','template1'))
                  AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_roles WHERE rolname<>'postgres' AND left(rolname,3)<>'pg_')
                """, connection);
            Assert.True(await command.ExecuteScalarAsync(ct) is true, "Fresh PG17/default durability admission failed.");
        }
        catch (Exception error) { failed = true; throw SafeFailure(error); }
    }

    public async Task DisposeAsync()
    {
        // CleanUp=false deliberately retains every owned container/data/network for
        // inspection. Only these exact newly owned containers are stopped; nothing is deleted.
        Exception? stopFailure = null;
        var postgres = container is { } ownedPostgres ? new[] { ownedPostgres } : Array.Empty<IContainer>();
        foreach (var owned in setupHelpers.Concat(postgres))
        {
            using var stopBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await owned.StopAsync(stopBudget.Token); }
            catch (Exception error) { stopFailure ??= error; }
        }
        if (stopFailure is not null) throw SafeFailure(stopFailure);
    }

    internal async Task RunAsync(string name, ITestOutputHelper output, Func<AuthOnboardingAtomicDatabase, CancellationToken, Task> run)
    {
        if (failed) throw new InvalidOperationException("PREVIOUS_ATOMIC_PROVIDER_CASE_FAILED_NO_FURTHER_DATABASE_WORK");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            output.WriteLine(JsonSerializer.Serialize(new { scenario = name, fixtureId = id, project,
                containerId = container!.Id, network = network!.Name, volume = volume!.Name,
                image = "postgres:17-alpine", hostBinding = "127.0.0.1", port, retained = true,
                dedicatedBridge = true, unrelatedContainersAttached = false, outboundInternal = false }));
            stage = "fresh-auth-database-baseline:" + name;
            var database = await CreateDatabaseAsync(deadline.Token);
            await database.PrepareBaselineAsync(deadline.Token);
            stage = "provider-scenario:" + name;
            await run(database, deadline.Token);
            output.WriteLine(JsonSerializer.Serialize(new { scenario = name, outcome = "PASS", database = database.Name,
                fullVerifier = "production-auth-v20", actualPostgreSqlMajor = 17, noRetainedResourceAccess = true }));
        }
        catch (Exception error) { failed = true; throw SafeFailure(error); }
    }

    internal async Task<AuthOnboardingAtomicDatabase> CreateDatabaseAsync(CancellationToken ct)
    {
        var databaseName = "auth_atomic_" + Guid.NewGuid().ToString("N");
        await using var admin = await OpenAdminAsync("postgres", ct);
        await using var create = new NpgsqlCommand("CREATE DATABASE " + Quote(databaseName), admin);
        await create.ExecuteNonQueryAsync(ct);
        return new AuthOnboardingAtomicDatabase(this, databaseName);
    }

    internal AuthOnboardingAtomicDatabase InitializedAuthDatabase() => new(this, "zeka_m1_auth");

    internal async Task RunInitializationAsync(string name, ITestOutputHelper output, Func<CancellationToken, Task> run)
    {
        if (failed) throw new InvalidOperationException("PREVIOUS_ATOMIC_PROVIDER_CASE_FAILED_NO_FURTHER_DATABASE_WORK");
        // Two five-minute setup invocations plus bounded admission/readback; this is a test
        // operation deadline, not a replacement owner task budget.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            stage = "actual-local-setup-cli:" + name;
            output.WriteLine(JsonSerializer.Serialize(new { scenario = name, fixtureId = id, project,
                containerId = container!.Id, network = network!.Name, volume = volume!.Name,
                retainedAfterStop = true, reaperCleanupClaimed = false }));
            await run(deadline.Token);
        }
        catch (Exception error) { failed = true; throw SafeFailure(error); }
    }

    internal async Task AdmitSetupImageAsync(AuthSetupImageBinding binding, CancellationToken ct)
    {
        // Labels correlate the separately admitted build; they are not an independent
        // source-admission attestation and never authorize an implicit pull or build.
        const string format = "{\"id\":{{json .Id}},\"labels\":{{json .Config.Labels}},\"user\":{{json .Config.User}},\"workdir\":{{json .Config.WorkingDir}},\"entrypoint\":{{json .Config.Entrypoint}}}";
        var image = await DockerJsonAsync(["image", "inspect", "--format", format, binding.Image], ct);
        if (image.GetProperty("id").GetString() != binding.Image
            || image.GetProperty("labels").GetProperty("org.opencontainers.image.revision").GetString() != binding.SourceCommit
            || image.GetProperty("labels").GetProperty("io.zeka.owner-source-graph-sha256").GetString() != binding.SourceGraphSha256
            || image.GetProperty("user").GetString() is not ("app" or "1654" or "1654:1654")
            || image.GetProperty("workdir").GetString() != "/app"
            || !image.GetProperty("entrypoint").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(new[] { "dotnet", "zeka-local-db-setup.dll" }, StringComparer.Ordinal))
            throw new InvalidOperationException("SETUP_CLI_IMAGE_PROVENANCE_REJECTED");
    }

    internal async Task<IContainer> StartSetupHelperAsync(AuthSetupImageBinding binding, SetupRequest request, CancellationToken ct)
    {
        if (network is null || container is null) throw new InvalidOperationException("SETUP_CLI_FIXTURE_NOT_INITIALIZED");
        var runtime = new NpgsqlConnectionStringBuilder
        {
            Host = "postgres", Port = 5432, Database = "zeka_m1_auth", Username = "zeka_auth_runtime",
            Password = runtimeSecret, Pooling = false, Timeout = 5, CommandTimeout = 30,
            SslMode = SslMode.Disable, IncludeErrorDetail = false
        };
        var privateInputs = new Dictionary<string, byte[]>
        {
            ["/run/setup/request.json"] = JsonSerializer.SerializeToUtf8Bytes(request),
            ["/run/secrets/admin-password"] = Encoding.UTF8.GetBytes(adminSecret),
            ["/run/secrets/migrator-password"] = Encoding.UTF8.GetBytes(migratorSecret),
            ["/run/secrets/runtime-password"] = Encoding.UTF8.GetBytes(runtimeSecret),
            ["/run/secrets/runtime-config.json"] = JsonSerializer.SerializeToUtf8Bytes(new
                { ConnectionStrings = new Dictionary<string, string> { ["Default"] = runtime.ConnectionString } })
        };
        try
        {
            var builder = new ContainerBuilder().WithImage(binding.Image).WithImagePullPolicy(_ => false)
                .WithLogger(NullLogger.Instance)
                .WithName(project + "-setup-" + Guid.NewGuid().ToString("N"))
                .WithLabel("com.diliwo.zeka.fixture-id", id)
                .WithLabel("com.diliwo.zeka.scope", "auth-local-setup-cli-provider-tests")
                .WithNetwork(network).WithCleanUp(false).WithAutoRemove(false)
                // Keep the owned helper alive only to inspect real CLI receipts before its
                // private tmpfs is discarded. ExecAsync below invokes the actual Program.
                .WithEntrypoint("/bin/sh").WithCommand("-c", "trap 'exit 0' TERM INT; sleep 600 & wait")
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.User = "1654:1654";
                    // Preserve the setup image's writable rootfs for Testcontainers'
                    // pre-start archive delivery. These are protected copied files,
                    // not read-only mounts; no secret enters an argument or environment.
                    parameters.HostConfig.ReadonlyRootfs = false;
                    parameters.HostConfig.Privileged = false;
                    parameters.HostConfig.CapDrop = ["ALL"];
                    parameters.HostConfig.SecurityOpt = ["no-new-privileges:true"];
                    parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                    {
                        ["/tmp"] = "rw,noexec,nosuid,nodev,mode=0700,uid=1654,gid=1654,size=64m",
                        ["/run/results"] = "rw,noexec,nosuid,nodev,mode=0700,uid=1654,gid=1654,size=16m"
                    };
                })
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(["test", "-r", "/app/zeka-local-db-setup.dll"]));
            foreach (var (path, bytes) in privateInputs)
                builder = builder.WithResourceMapping(bytes, path, uid: 1654, gid: 1654, fileMode: UnixFileModes.UserRead);
            var source = Path.Combine(FindRepository(), "Deployments/database/bootstrap-auth-roles.sql");
            builder = builder.WithResourceMapping(new FileInfo(source), new FileInfo("/run/source/bootstrap-auth-roles.sql"),
                uid: 1654, gid: 1654, fileMode: UnixFileModes.UserRead);
            var helper = builder.Build();
            setupHelpers.Add(helper);
            var expectedHashes = privateInputs.ToDictionary(input => input.Key, input => SHA256.HashData(input.Value), StringComparer.Ordinal);
            expectedHashes.Add("/run/source/bootstrap-auth-roles.sql", SHA256.HashData(await File.ReadAllBytesAsync(source, ct)));
            using var start = CancellationTokenSource.CreateLinkedTokenSource(ct);
            start.CancelAfter(TimeSpan.FromMinutes(2));
            await helper.StartAsync(start.Token);
            // Testcontainers assigns the Docker ID when creating, not while building.
            setupInputHashes[helper.Id] = expectedHashes;
            await VerifySetupInputsAsync(helper, ct);
            const string format = "{\"id\":{{json .Id}},\"image\":{{json .Image}},\"user\":{{json .Config.User}},\"env\":{{json .Config.Env}},\"ports\":{{json .NetworkSettings.Ports}},\"networks\":{{json .NetworkSettings.Networks}},\"privileged\":{{json .HostConfig.Privileged}},\"readonly\":{{json .HostConfig.ReadonlyRootfs}},\"binds\":{{json .HostConfig.Binds}},\"tmpfs\":{{json .HostConfig.Tmpfs}}}";
            var actual = await DockerJsonAsync(["inspect", "--format", format, helper.Id], ct);
            Assert.Equal(binding.Image, actual.GetProperty("image").GetString());
            Assert.Equal("1654:1654", actual.GetProperty("user").GetString());
            Assert.False(actual.GetProperty("privileged").GetBoolean());
            Assert.False(actual.GetProperty("readonly").GetBoolean());
            Assert.True(actual.GetProperty("binds").ValueKind == JsonValueKind.Null
                || actual.GetProperty("binds").GetArrayLength() == 0);
            Assert.True(actual.GetProperty("ports").ValueKind == JsonValueKind.Null
                || actual.GetProperty("ports").EnumerateObject().All(port => port.Value.ValueKind == JsonValueKind.Null
                    || port.Value.GetArrayLength() == 0));
            Assert.Equal(network.Name, Assert.Single(actual.GetProperty("networks").EnumerateObject()).Name);
            Assert.Equal(new[] { "/run/results", "/tmp" }, actual.GetProperty("tmpfs").EnumerateObject()
                .Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal));
            AssertNoCredentialMaterial(actual.GetProperty("env").GetRawText());
            return helper;
        }
        finally { foreach (var bytes in privateInputs.Values) CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task AdmitAuthCatalogPostgresImageAsync(AuthCatalogPostgresBinding binding, CancellationToken ct)
    {
        // Any unsuccessful replacement admission invalidates the previous capability.
        admittedAuthCatalogPostgresBinding = null;
        observedAuthCatalogPostgresImage = null;
        if (!AuthCatalogTransportRequested || network is not null || volume is not null || container is not null
            || setupHelpers.Count != 0)
            throw new InvalidOperationException("AUTH_CATALOG_POSTGRES_ADMISSION_MUST_PRECEDE_RESOURCES");
        RequireAuthCatalogLocalEngine();
        AuthCatalogPostgresBinding.FromRequiredInputs(binding.Image, binding.RepoDigest);
        const string format = "{\"id\":{{json .Id}},\"os\":{{json .Os}},\"architecture\":{{json .Architecture}},\"repoDigests\":{{json .RepoDigests}}}";
        var image = await DockerJsonAsync(["image", "inspect", "--format", format, binding.Image], ct);
        binding.RequireLocalImage(image);
        admittedAuthCatalogPostgresBinding = binding;
    }

    private async Task VerifyAuthCatalogPostgresImageAsync(CancellationToken ct)
    {
        const string format = "{\"id\":{{json .Id}},\"image\":{{json .Image}}}";
        if (container is null) throw new InvalidOperationException("AUTH_CATALOG_OWNED_POSTGRES_REQUIRED");
        var observed = await DockerJsonAsync(["inspect", "--format", format, container.Id], ct);
        ObserveAuthCatalogPostgresImage(observed);
    }

    private void ObserveAuthCatalogPostgresImage(JsonElement observed)
    {
        var binding = admittedAuthCatalogPostgresBinding
            ?? throw new InvalidOperationException("AUTH_CATALOG_POSTGRES_ADMISSION_REQUIRED");
        AuthCatalogRunnerBinding.Require(observed.ValueKind == JsonValueKind.Object,
            "AUTH_CATALOG_OWNED_POSTGRES_METADATA_REJECTED");
        AuthCatalogRunnerBinding.RequireUnique(observed);
        AuthCatalogRunnerBinding.Require(observed.TryGetProperty("id", out var actualId)
            && actualId.ValueKind == JsonValueKind.String && actualId.GetString() == container!.Id,
            "AUTH_CATALOG_OWNED_POSTGRES_ID_REJECTED");
        AuthCatalogRunnerBinding.Require(observed.TryGetProperty("image", out var actualImage)
            && actualImage.ValueKind == JsonValueKind.String, "AUTH_CATALOG_OWNED_POSTGRES_IMAGE_REJECTED");
        observedAuthCatalogPostgresImage = binding.RequireCreatedImage(actualImage.GetString());
    }

    internal async Task AdmitAuthCatalogImageAsync(AuthCatalogRunnerBinding binding, CancellationToken ct)
    {
        if (!AuthCatalogTransportRequested) throw new InvalidOperationException("AUTH_CATALOG_TRANSPORT_FLAG_REQUIRED");
        RequireAuthCatalogLocalEngine();
        ValidateAuthCatalogBinding(binding);
        // A vendor SDK image has no Zeka source labels. The exact local image identity
        // is admitted separately from the copied, hash-bound Auth payload.
        const string format = "{\"id\":{{json .Id}},\"os\":{{json .Os}},\"architecture\":{{json .Architecture}},\"env\":{{json .Config.Env}},\"volumes\":{{json (index .Config \"Volumes\")}},\"repoDigests\":{{json .RepoDigests}}}";
        var image = await DockerJsonAsync(["image", "inspect", "--format", format, binding.Image], ct);
        if (image.GetProperty("id").GetString() != binding.Image
            || image.GetProperty("os").GetString() != "linux"
            || image.GetProperty("architecture").GetString() != "amd64"
            || (image.GetProperty("volumes").ValueKind != JsonValueKind.Null
                && image.GetProperty("volumes").EnumerateObject().Any()))
            throw new InvalidOperationException("AUTH_CATALOG_SDK_IMAGE_REJECTED");
        var repoDigests = image.GetProperty("repoDigests");
        if (repoDigests.ValueKind != JsonValueKind.Array || repoDigests.GetArrayLength() == 0
            || !repoDigests.EnumerateArray().Any(value => value.GetString() is { } digest
                && digest.StartsWith("mcr.microsoft.com/dotnet/sdk@sha256:", StringComparison.Ordinal)
                && digest["mcr.microsoft.com/dotnet/sdk@sha256:".Length..] is { Length: 64 } hash
                && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidOperationException("AUTH_CATALOG_VENDOR_SDK_DIGEST_REJECTED");
        AssertAuthCatalogEnvironment(image.GetProperty("env"));
        if (!image.GetProperty("env").EnumerateArray().Any(value =>
            value.GetString() == "DOTNET_SDK_VERSION=" + binding.SdkVersion))
            throw new InvalidOperationException("AUTH_CATALOG_SDK_VERSION_METADATA_REJECTED");
        admittedAuthCatalogBinding = binding;
    }

    internal async Task<IContainer> StartAuthCatalogRunnerAsync(AuthCatalogRunnerBinding binding, CancellationToken ct)
    {
        if (!AuthCatalogTransportRequested || network is null || container is null
            || admittedAuthCatalogBinding != binding)
            throw new InvalidOperationException("AUTH_CATALOG_RUNNER_NOT_ADMITTED");
        RequireAuthCatalogLocalEngine();
        await VerifyAuthCatalogPostgresImageAsync(ct);
        ValidateAuthCatalogBinding(binding);
        var secretDirectory = "/run/secrets/auth-ef8-" + id;
        var resultDirectory = "/run/results/auth-ef8-" + id;
        var privateInputs = new Dictionary<string, byte[]>
        {
            ["request.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1, runId = id, host = "postgres-" + id, port = 5432,
                database = "postgres", username = "postgres",
                adminPasswordFile = secretDirectory + "/admin-password",
                migratorPasswordFile = secretDirectory + "/migrator-password",
                runtimePasswordFile = secretDirectory + "/runtime-password",
                snapshotFile = resultDirectory + "/reference.json",
                sourceCommit = binding.SourceCommit, sourceTree = binding.SourceTree,
                expectedTestAssemblySha256 = binding.TestAssemblySha256
            }),
            ["admin-password"] = Encoding.UTF8.GetBytes(adminSecret),
            ["migrator-password"] = Encoding.UTF8.GetBytes(migratorSecret),
            ["runtime-password"] = Encoding.UTF8.GetBytes(runtimeSecret)
        };
        var payloadBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            var totalBytes = 0L;
            foreach (var (relative, expectedHash) in binding.PayloadHashes)
            {
                var source = AuthCatalogPayloadSource(binding.PayloadRoot, relative);
                var info = new FileInfo(source);
                if (!info.Exists || info.Length > 64 * 1024 * 1024)
                    throw new InvalidOperationException("AUTH_CATALOG_PAYLOAD_FILE_REJECTED");
                var bytes = await File.ReadAllBytesAsync(source, ct);
                payloadBytes.Add(relative, bytes);
                totalBytes += bytes.Length;
                if (bytes.Length > 64 * 1024 * 1024 || totalBytes > 512L * 1024 * 1024
                    || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHash), SHA256.HashData(bytes)))
                    throw new InvalidOperationException("AUTH_CATALOG_PAYLOAD_IDENTITY_REJECTED");
            }
            var builder = new ContainerBuilder().WithDockerEndpoint(AuthCatalogDockerEndpoint)
                .WithImage(binding.Image).WithImagePullPolicy(_ => false)
                .WithLogger(NullLogger.Instance)
                .WithName(project + "-auth-ef8-" + Guid.NewGuid().ToString("N"))
                .WithLabel("com.diliwo.zeka.fixture-id", id)
                .WithLabel("com.diliwo.zeka.scope", "auth-ef8-catalog-database-only-runner")
                .WithLabel("com.diliwo.zeka.source-commit", binding.SourceCommit)
                .WithLabel("com.diliwo.zeka.source-tree", binding.SourceTree)
                .WithLabel("com.diliwo.zeka.payload-manifest-sha256", binding.PayloadManifestSha256)
                .WithNetwork(network).WithCleanUp(false).WithAutoRemove(false)
                .WithEnvironment("ZEKA_AUTH_EF8_FIXTURE_MODE", "external-file")
                .WithEnvironment("ZEKA_AUTH_EF8_FIXTURE_CONFIG_FILE", secretDirectory + "/request.json")
                .WithEnvironment("ZEKA_AUTH_EF8_FIXTURE_RUN_ID", id)
                .WithEnvironment("ZEKA_AUTH_EF8_FIXTURE_SOURCE_TREE", binding.SourceTree)
                .WithEnvironment("ZEKA_AUTH_EF8_CATALOG_SNAPSHOT_FILE", resultDirectory + "/reference.json")
                .WithEnvironment("HOME", "/tmp/auth-home")
                .WithEnvironment("DOTNET_CLI_HOME", "/tmp/auth-home")
                .WithEnvironment("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
                .WithEnvironment("DOTNET_CLI_TELEMETRY_OPTOUT", "1")
                .WithEnvironment("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false")
                .WithEnvironment("DOTNET_NOLOGO", "1")
                .WithEntrypoint("/bin/sh").WithCommand("-c", "trap 'exit 0' TERM INT; sleep 600 & wait")
                .WithCreateParameterModifier(parameters =>
                {
                    parameters.HostConfig.NetworkMode = network.Name;
                    parameters.User = "1654:1654";
                    parameters.WorkingDir = "/app";
                    // Pre-start archive delivery creates protected copies in the writable
                    // rootfs; post-start copies enter private tmpfs without secret argv.
                    parameters.HostConfig.ReadonlyRootfs = false;
                    parameters.HostConfig.Privileged = false;
                    parameters.HostConfig.CapDrop = ["ALL"];
                    parameters.HostConfig.SecurityOpt = ["no-new-privileges:true"];
                    parameters.HostConfig.Tmpfs = new Dictionary<string, string>
                    {
                        ["/tmp"] = "rw,noexec,nosuid,nodev,mode=0700,uid=1654,gid=1654,size=128m",
                        ["/run/secrets"] = "rw,noexec,nosuid,nodev,mode=0700,uid=1654,gid=1654,size=1m",
                        ["/run/results"] = "rw,noexec,nosuid,nodev,mode=0700,uid=1654,gid=1654,size=32m"
                    };
                })
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                    ["test", "-r", "/app/tests/Infrastructure.IntegrationTests.dll"]));
            foreach (var (relative, bytes) in payloadBytes)
                builder = builder.WithResourceMapping(bytes, "/app/" + relative,
                    uid: 1654, gid: 1654, fileMode: UnixFileModes.UserRead);
            var expectedPrivateHashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (file, bytes) in privateInputs)
            {
                var input = "/run/input/auth-ef8-" + id + "-" + file;
                builder = builder.WithResourceMapping(bytes, input,
                    uid: 1654, gid: 1654, fileMode: UnixFileModes.UserRead);
                expectedPrivateHashes.Add(input, SHA256.HashData(bytes));
                expectedPrivateHashes.Add(secretDirectory + "/" + file, SHA256.HashData(bytes));
            }
            var helper = builder.Build();
            // Register every newly owned container before start so failed admission is
            // still stopped and retained by the fixture's existing cleanup boundary.
            setupHelpers.Add(helper);
            using var start = CancellationTokenSource.CreateLinkedTokenSource(ct);
            start.CancelAfter(TimeSpan.FromMinutes(2));
            await helper.StartAsync(start.Token);
            setupInputHashes[helper.Id] = expectedPrivateHashes;
            await AssertAuthCatalogRunnerAsync(helper, binding, ct);
            var directories = await helper.ExecAsync(
                ["/usr/bin/mkdir", "-m", "0700", "--", secretDirectory, resultDirectory, "/tmp/auth-home"], ct);
            Assert.Equal(0, directories.ExitCode);
            foreach (var file in privateInputs.Keys)
            {
                var copy = await helper.ExecAsync(["/usr/bin/cp", "--preserve=mode", "--",
                    "/run/input/auth-ef8-" + id + "-" + file, secretDirectory + "/" + file], ct);
                Assert.Equal(0, copy.ExitCode);
            }
            await VerifyAuthCatalogInputsAsync(helper, ct);
            var links = await helper.ExecAsync(new[] { "/usr/bin/stat", "-c", "%u:%g:%a:%F:%h", "--" }
                .Concat(expectedPrivateHashes.Keys).ToArray(), ct);
            Assert.Equal(0, links.ExitCode);
            Assert.Equal(Enumerable.Repeat("1654:1654:400:regular file:1", expectedPrivateHashes.Count),
                links.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var modes = await helper.ExecAsync(["/usr/bin/stat", "-c", "%u:%g:%a:%F", "--",
                "/run/secrets", secretDirectory, resultDirectory, "/tmp/auth-home"], ct);
            Assert.Equal(0, modes.ExitCode);
            Assert.Equal(Enumerable.Repeat("1654:1654:700:directory", 4),
                modes.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            await VerifyAuthCatalogPayloadAsync(helper, binding, ct);
            var sdk = await helper.ExecAsync(["dotnet", "--version"], ct);
            AssertNoCredentialMaterial(sdk.Stdout);
            AssertNoCredentialMaterial(sdk.Stderr);
            Assert.Equal(0, sdk.ExitCode);
            Assert.Equal(binding.SdkVersion, sdk.Stdout.Trim());
            var runtimes = await helper.ExecAsync(["dotnet", "--list-runtimes"], ct);
            AssertNoCredentialMaterial(runtimes.Stdout);
            AssertNoCredentialMaterial(runtimes.Stderr);
            Assert.Equal(0, runtimes.ExitCode);
            Assert.Contains("Microsoft.NETCore.App 8.", runtimes.Stdout, StringComparison.Ordinal);
            Assert.Contains("Microsoft.AspNetCore.App 8.", runtimes.Stdout, StringComparison.Ordinal);
            authCatalogHelpers.Add(helper.Id);
            return helper;
        }
        finally
        {
            foreach (var bytes in privateInputs.Values.Concat(payloadBytes.Values))
                CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal async Task VerifyAuthCatalogInputsAsync(IContainer helper, CancellationToken ct)
    {
        if (helper is null || !setupHelpers.Any(owned => ReferenceEquals(owned, helper))
            || !setupInputHashes.TryGetValue(helper.Id, out var expected))
            throw new InvalidOperationException("AUTH_CATALOG_PRIVATE_INPUT_CUSTODY_REQUIRED");
        foreach (var (path, digest) in expected)
        {
            var metadata = await helper.ExecAsync(["/usr/bin/stat", "-c", "%u:%g:%a:%F:%h:%s", "--", path], ct);
            Assert.Equal(0, metadata.ExitCode);
            var fields = metadata.Stdout.Trim().Split(':');
            Assert.Equal(6, fields.Length);
            Assert.Equal(new[] { "1654", "1654", "400", "regular file", "1" }, fields[..5]);
            Assert.True(int.TryParse(fields[5], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) && size > 0 && size <= 4096,
                "Protected Auth input length exceeded the reviewed bound.");
            var resolved = await helper.ExecAsync(["/usr/bin/readlink", "-f", "--", path], ct);
            Assert.Equal(0, resolved.ExitCode);
            Assert.Equal(path, resolved.Stdout.Trim());
            // Docker archive reads cannot export tmpfs. This private transport is kept
            // in memory; neither encoded bytes, credential values nor their hashes leave it.
            var encoded = await helper.ExecAsync(["/usr/bin/base64", "-w0", "--", path], ct);
            Assert.Equal(0, encoded.ExitCode);
            Assert.True(encoded.Stdout.Length == ((size + 2) / 3) * 4,
                "Protected Auth input encoding length changed.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded.Stdout); }
            catch (FormatException) { throw new InvalidOperationException("AUTH_CATALOG_PRIVATE_INPUT_ENCODING_REJECTED"); }
            try
            {
                Assert.True(bytes.Length == size
                    && CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(bytes)),
                    "Protected Auth input bytes changed; values and digests are not exported.");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    internal static bool IsAuthCatalogEvidenceFile(string? file)
        => file is "reference.json" or "producer.trx";

    internal async Task<byte[]> ReadAuthCatalogEvidenceAsync(IContainer helper, string file, CancellationToken ct)
    {
        if (!IsAuthCatalogEvidenceFile(file) || helper is null || !authCatalogHelpers.Contains(helper.Id))
            throw new InvalidOperationException("AUTH_CATALOG_EVIDENCE_PATH_REJECTED");
        var directory = "/run/results/auth-ef8-" + id;
        var privateDirectory = await helper.ExecAsync(["/usr/bin/stat", "-c", "%u:%g:%a:%F", "--", directory], ct);
        if (privateDirectory.ExitCode != 0 || privateDirectory.Stdout.Trim() != "1654:1654:700:directory")
            throw new InvalidOperationException("AUTH_CATALOG_EVIDENCE_DIRECTORY_REJECTED");
        var path = directory + "/" + file;
        var metadata = await helper.ExecAsync(["/usr/bin/stat", "-c", "%u:%g:%a:%F:%h", "--", path], ct);
        if (metadata.ExitCode != 0 || metadata.Stdout.Trim() != "1654:1654:600:regular file:1")
            throw new InvalidOperationException("AUTH_CATALOG_EVIDENCE_FILE_REJECTED");
        // The catalogue preserves every metadata value; its bound is separate from
        // the existing setup JSON contract. TRX is bounded independently.
        return await ReadOwnedEvidenceAsync(helper, path,
            file == "reference.json" ? 16 * 1024 * 1024 : 4 * 1024 * 1024, ct);
    }

    internal static bool IsAuthCatalogPayloadPath(string? path)
    {
        if (path is null || path.Length > 240
            || !path.All(value => char.IsAsciiLetterOrDigit(value) || value is '/' or '_' or '-' or '.'))
            return false;
        var segments = path.Split('/');
        return segments.All(segment => segment.Length > 0 && segment is not ("." or "..")
                && !segment.EndsWith('.') && !Regex.IsMatch(segment.Split('.')[0],
                    @"^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(50)))
            && (path == "Deployments/database/bootstrap-auth-roles.sql"
                || (path.StartsWith("tests/", StringComparison.Ordinal) && segments.Length > 1));
    }

    private static void ValidateAuthCatalogBinding(AuthCatalogRunnerBinding binding)
    {
        static bool LowerHex(string? value, int count) => value?.Length == count
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        if (!binding.Image.StartsWith("sha256:", StringComparison.Ordinal) || !LowerHex(binding.Image[7..], 64)
            || !LowerHex(binding.SourceCommit, 40) || !LowerHex(binding.SourceTree, 40)
            || !LowerHex(binding.TestAssemblySha256, 64) || !LowerHex(binding.PayloadManifestSha256, 64)
            || !AuthCatalogRunnerBinding.IsStableNet8Sdk(binding.SdkVersion)
            || !Path.IsPathFullyQualified(binding.PayloadRoot)
            || Path.GetFullPath(binding.PayloadRoot) != binding.PayloadRoot
            || !Directory.Exists(binding.PayloadRoot)
            || binding.PayloadHashes.Count is 0 or > 512)
            throw new InvalidOperationException("AUTH_CATALOG_BINDING_REJECTED");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, hash) in binding.PayloadHashes)
            if (!IsAuthCatalogPayloadPath(path) || !LowerHex(hash, 64) || !paths.Add(path))
                throw new InvalidOperationException("AUTH_CATALOG_PAYLOAD_MANIFEST_REJECTED");
        foreach (var required in new[]
        {
            "tests/Infrastructure.IntegrationTests.dll", "tests/Infrastructure.IntegrationTests.deps.json",
            "tests/Infrastructure.IntegrationTests.runtimeconfig.json", "tests/AuthManager.Infrastructure.dll",
            "tests/Microsoft.EntityFrameworkCore.dll", "tests/Npgsql.EntityFrameworkCore.PostgreSQL.dll",
            "tests/testhost.dll", "tests/xunit.execution.dotnet.dll",
            "tests/xunit.runner.visualstudio.dotnetcore.testadapter.dll",
            "Deployments/database/bootstrap-auth-roles.sql"
        })
            if (!binding.PayloadHashes.ContainsKey(required))
                throw new InvalidOperationException("AUTH_CATALOG_REQUIRED_PAYLOAD_MISSING");
        if (binding.PayloadHashes["tests/Infrastructure.IntegrationTests.dll"] != binding.TestAssemblySha256)
            throw new InvalidOperationException("AUTH_CATALOG_TEST_ASSEMBLY_BINDING_REJECTED");
    }

    private static string AuthCatalogPayloadSource(string root, string relative)
    {
        var source = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!source.StartsWith(prefix, comparison))
            throw new InvalidOperationException("AUTH_CATALOG_PAYLOAD_PATH_REJECTED");
        for (FileSystemInfo? entry = new FileInfo(source); entry is not null;
            entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null)
                throw new InvalidOperationException("AUTH_CATALOG_PAYLOAD_LINK_REJECTED");
        return source;
    }

    private async Task AssertAuthCatalogRunnerAsync(IContainer helper, AuthCatalogRunnerBinding binding, CancellationToken ct)
    {
        const string format = "{\"id\":{{json .Id}},\"image\":{{json .Image}},\"user\":{{json .Config.User}},\"workdir\":{{json .Config.WorkingDir}},\"labels\":{{json .Config.Labels}},\"env\":{{json .Config.Env}},\"ports\":{{json .NetworkSettings.Ports}},\"networks\":{{json .NetworkSettings.Networks}},\"privileged\":{{json .HostConfig.Privileged}},\"readonly\":{{json .HostConfig.ReadonlyRootfs}},\"binds\":{{json .HostConfig.Binds}},\"mounts\":{{json .Mounts}},\"tmpfs\":{{json .HostConfig.Tmpfs}},\"networkmode\":{{json .HostConfig.NetworkMode}},\"capdrop\":{{json .HostConfig.CapDrop}},\"securityopt\":{{json .HostConfig.SecurityOpt}}}";
        var actual = await DockerJsonAsync(["inspect", "--format", format, helper.Id], ct);
        Assert.Equal(helper.Id, actual.GetProperty("id").GetString());
        Assert.Equal(binding.Image, actual.GetProperty("image").GetString());
        Assert.Equal("1654:1654", actual.GetProperty("user").GetString());
        Assert.Equal("/app", actual.GetProperty("workdir").GetString());
        var labels = actual.GetProperty("labels");
        Assert.Equal(id, labels.GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.Equal(binding.SourceCommit, labels.GetProperty("com.diliwo.zeka.source-commit").GetString());
        Assert.Equal(binding.SourceTree, labels.GetProperty("com.diliwo.zeka.source-tree").GetString());
        Assert.Equal(binding.PayloadManifestSha256, labels.GetProperty("com.diliwo.zeka.payload-manifest-sha256").GetString());
        Assert.False(actual.GetProperty("privileged").GetBoolean());
        Assert.False(actual.GetProperty("readonly").GetBoolean());
        Assert.True(actual.GetProperty("binds").ValueKind == JsonValueKind.Null
            || actual.GetProperty("binds").GetArrayLength() == 0);
        Assert.True(actual.GetProperty("ports").ValueKind == JsonValueKind.Null
            || actual.GetProperty("ports").EnumerateObject().All(port => port.Value.ValueKind == JsonValueKind.Null
                || port.Value.GetArrayLength() == 0));
        Assert.Equal(network!.Name, Assert.Single(actual.GetProperty("networks").EnumerateObject()).Name);
        Assert.Equal(network.Name, actual.GetProperty("networkmode").GetString());
        var tmpfs = new[] { "/run/results", "/run/secrets", "/tmp" };
        Assert.Equal(tmpfs, actual.GetProperty("tmpfs").EnumerateObject()
            .Select(value => value.Name).OrderBy(value => value, StringComparer.Ordinal));
        Assert.All(actual.GetProperty("mounts").EnumerateArray(), mount =>
        {
            Assert.Equal("tmpfs", mount.GetProperty("Type").GetString());
            Assert.Contains(mount.GetProperty("Destination").GetString(), tmpfs);
        });
        Assert.Equal(new[] { "ALL" }, actual.GetProperty("capdrop").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("no-new-privileges:true",
            actual.GetProperty("securityopt").EnumerateArray().Select(value => value.GetString()));
        AssertAuthCatalogEnvironment(actual.GetProperty("env"));
        var environment = actual.GetProperty("env").EnumerateArray().Select(value => value.GetString()!).ToArray();
        foreach (var expected in new[]
        {
            "ZEKA_AUTH_EF8_FIXTURE_MODE=external-file",
            "ZEKA_AUTH_EF8_FIXTURE_CONFIG_FILE=/run/secrets/auth-ef8-" + id + "/request.json",
            "ZEKA_AUTH_EF8_FIXTURE_RUN_ID=" + id,
            "ZEKA_AUTH_EF8_FIXTURE_SOURCE_TREE=" + binding.SourceTree,
            "ZEKA_AUTH_EF8_CATALOG_SNAPSHOT_FILE=/run/results/auth-ef8-" + id + "/reference.json",
            "HOME=/tmp/auth-home", "DOTNET_CLI_HOME=/tmp/auth-home"
        })
            Assert.Contains(expected, environment, StringComparer.Ordinal);
    }

    private void AssertAuthCatalogEnvironment(JsonElement environment)
    {
        AssertNoCredentialMaterial(environment.GetRawText());
        foreach (var value in environment.EnumerateArray().Select(item => item.GetString()!))
        {
            var separator = value.IndexOf('=');
            if (separator <= 0 || Regex.IsMatch(value[..separator],
                "(?i)(?:PASSWORD|PWD|TOKEN|SECRET|CREDENTIAL|CONNECTIONSTRING|API_?KEY|ACCESS_?KEY|^OP_|(?:^|_)PAT(?:_|$)|^DOCKER_)",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                throw new InvalidOperationException("AUTH_CATALOG_CREDENTIAL_ENVIRONMENT_REJECTED");
        }
    }

    private static async Task VerifyAuthCatalogPayloadAsync(IContainer helper, AuthCatalogRunnerBinding binding, CancellationToken ct)
    {
        foreach (var batch in binding.PayloadHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal).Chunk(64))
        {
            var paths = batch.Select(pair => "/app/" + pair.Key).ToArray();
            var metadata = await helper.ExecAsync(new[] { "/usr/bin/stat", "-c", "%u:%g:%a:%F", "--" }.Concat(paths).ToArray(), ct);
            Assert.Equal(0, metadata.ExitCode);
            Assert.Equal(Enumerable.Repeat("1654:1654:400:regular file", paths.Length),
                metadata.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var resolved = await helper.ExecAsync(new[] { "/usr/bin/readlink", "-f", "--" }.Concat(paths).ToArray(), ct);
            Assert.Equal(0, resolved.ExitCode);
            Assert.Equal(paths, resolved.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            var hashes = await helper.ExecAsync(new[] { "/usr/bin/sha256sum", "--" }.Concat(paths).ToArray(), ct);
            Assert.Equal(0, hashes.ExitCode);
            Assert.Equal(batch.Select(pair => pair.Value + "  /app/" + pair.Key),
                hashes.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    internal static bool IsSetupEvidencePath(string? path)
    {
        const string resultsPrefix = "/run/results/";
        const string migrationPrefix = "/tmp/zeka-m1-migration-";
        // A public JSON basename is one Linux filename, never a directory/alias.
        if (path is null || path.Length > resultsPrefix.Length + 255 || path.Any(char.IsControl)) return false;
        if (path.StartsWith(resultsPrefix, StringComparison.Ordinal))
        {
            var file = path[resultsPrefix.Length..];
            if (file.Length > 255 || !file.EndsWith(".json", StringComparison.Ordinal)) return false;
            var stem = file[..^5];
            return stem.Length > 0 && stem.All(value => char.IsAsciiLetterOrDigit(value) || value is '_' or '-');
        }
        if (!path.StartsWith(migrationPrefix, StringComparison.Ordinal)) return false;
        var remainder = path[migrationPrefix.Length..];
        if (remainder.Length <= 33 || remainder[32] != '/') return false;
        var directoryId = remainder[..32];
        if (!Guid.TryParseExact(directoryId, "N", out var operationId) || operationId == Guid.Empty
            || directoryId != operationId.ToString("N")) return false;
        var fileName = remainder[33..];
        return fileName == "plan-" + operationId.ToString("D") + ".json"
            || fileName == "apply-" + operationId.ToString("D") + ".json";
    }

    internal async Task<byte[]> ReadSetupEvidenceAsync(IContainer helper, string path, CancellationToken ct)
    {
        if (!IsSetupEvidencePath(path)) throw new InvalidOperationException("SETUP_EVIDENCE_PATH_REJECTED");
        return await ReadOwnedEvidenceAsync(helper, path, 1024 * 1024, ct);
    }

    private async Task<byte[]> ReadOwnedEvidenceAsync(IContainer helper, string path, int maximumBytes, CancellationToken ct)
    {
        if (helper is null || !setupHelpers.Any(owned => ReferenceEquals(owned, helper))
            || !setupInputHashes.ContainsKey(helper.Id))
            throw new InvalidOperationException("SETUP_EVIDENCE_HELPER_NOT_ADMITTED");
        var metadata = await helper.ExecAsync(["stat", "-c", "%u:%g:%f:%s", "--", path], ct);
        if (metadata.ExitCode != 0)
            throw new InvalidOperationException("SETUP_EVIDENCE_STAT_FAILED/exit=" + metadata.ExitCode);
        var fields = metadata.Stdout.Trim().Split(':');
        if (fields.Length != 4 || fields[0] != "1654" || fields[1] != "1654"
            || !uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var mode) || (mode & 0xF000) != 0x8000
            || !int.TryParse(fields[3], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var size) || size > maximumBytes)
            throw new InvalidOperationException("SETUP_EVIDENCE_FILE_REJECTED");
        var resolved = await helper.ExecAsync(["readlink", "-f", "--", path], ct);
        if (resolved.ExitCode != 0)
            throw new InvalidOperationException("SETUP_EVIDENCE_READLINK_FAILED/exit=" + resolved.ExitCode);
        if (resolved.Stdout.TrimEnd('\r', '\n') != path)
            throw new InvalidOperationException("SETUP_EVIDENCE_PATH_IDENTITY_REJECTED");
        // Docker's archive/copy API cannot export files on tmpfs. Keep the public
        // payload in memory; never log its base64 representation or decoded text.
        var encoded = await helper.ExecAsync(["base64", "-w0", "--", path], ct);
        if (encoded.ExitCode != 0)
            throw new InvalidOperationException("SETUP_EVIDENCE_READ_FAILED/exit=" + encoded.ExitCode);
        if (encoded.Stdout.Length != ((size + 2) / 3) * 4)
            throw new InvalidOperationException("SETUP_EVIDENCE_ENCODED_SIZE_REJECTED");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded.Stdout); }
        catch (FormatException) { throw new InvalidOperationException("SETUP_EVIDENCE_ENCODING_REJECTED"); }
        try
        {
            if (bytes.Length != size) throw new InvalidOperationException("SETUP_EVIDENCE_SIZE_MISMATCH");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidOperationException("SETUP_EVIDENCE_ENCODING_REJECTED"); }
            AssertNoCredentialMaterial(text);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    internal async Task VerifySetupInputsAsync(IContainer helper, CancellationToken ct)
    {
        if (!setupInputHashes.TryGetValue(helper.Id, out var expected))
            throw new InvalidOperationException("SETUP_PRIVATE_INPUT_CUSTODY_REQUIRED");
        foreach (var (path, digest) in expected)
        {
            var metadata = await helper.ExecAsync(["stat", "-c", "%u:%g:%a:%F", path], ct);
            Assert.Equal(0, metadata.ExitCode);
            Assert.Equal("1654:1654:400:regular file", metadata.Stdout.Trim());
            var resolved = await helper.ExecAsync(["readlink", "-f", path], ct);
            Assert.Equal(0, resolved.ExitCode);
            Assert.Equal(path, resolved.Stdout.Trim());
            var bytes = await helper.ReadFileAsync(path, ct);
            try { Assert.True(CryptographicOperations.FixedTimeEquals(digest, SHA256.HashData(bytes)),
                "Protected setup input bytes changed; values and digests are not exported."); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var modes = await helper.ExecAsync(["stat", "-c", "%u:%g:%a:%F", "/tmp", "/run/results"], ct);
        Assert.Equal(0, modes.ExitCode);
        Assert.Equal(new[] { "1654:1654:700:directory", "1654:1654:700:directory" },
            modes.Stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
    }

    internal void AssertNoCredentialMaterial(string value)
        => Assert.False(new[] { adminSecret, migratorSecret, runtimeSecret }.Any(secret => value.Contains(secret, StringComparison.Ordinal)),
            "Fixture credentials must not enter arguments, environment, diagnostics or exported evidence.");

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deployments/database/bootstrap-auth-roles.sql"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("SOURCE_BOOTSTRAP_NOT_FOUND");
    }

    Task<NpgsqlConnection> IAuthMigrationDatabaseHost.OpenAdminAsync(string database, CancellationToken ct) => OpenAdminAsync(database, ct);
    string IAuthMigrationDatabaseHost.MigratorConnection(string database) => MigratorConnection(database);
    string IAuthMigrationDatabaseHost.RuntimeConnection(string database) => RuntimeConnection(database);
    string IAuthMigrationDatabaseHost.RolePasswordSql() => RolePasswordSql();

    internal string MigratorConnection(string database) => Connection(database, "zeka_auth_migrator", migratorSecret);
    internal string RuntimeConnection(string database) => Connection(database, "zeka_auth_runtime", runtimeSecret);
    internal async Task<NpgsqlConnection> OpenAdminAsync(string database, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(Connection(database, "postgres", adminSecret));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    internal string RolePasswordSql() => "ALTER ROLE zeka_auth_migrator PASSWORD " + Literal(migratorSecret)
        + "; ALTER ROLE zeka_auth_runtime PASSWORD " + Literal(runtimeSecret) + ";";

    private string Connection(string database, string role, string secret) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = port, Database = database, Username = role, Password = secret,
        Pooling = false, Timeout = 5, CommandTimeout = 30, IncludeErrorDetail = false,
        ApplicationName = "zeka-auth-atomic-provider-" + id
    }.ConnectionString;

    private async Task InspectAsync(CancellationToken ct)
    {
        const string format = "{\"id\":{{json .Id}},\"image\":{{json .Image}},\"labels\":{{json .Config.Labels}},\"requestedPorts\":{{json .HostConfig.PortBindings}},\"ports\":{{json .NetworkSettings.Ports}},\"mounts\":{{json .Mounts}},\"networks\":{{json .NetworkSettings.Networks}},\"env\":{{json .Config.Env}},\"privileged\":{{json .HostConfig.Privileged}}}";
        var root = await DockerJsonAsync(["inspect", "--format", format, container!.Id], ct);
        Assert.Equal(container.Id, root.GetProperty("id").GetString());
        if (AuthCatalogTransportRequested) ObserveAuthCatalogPostgresImage(root);
        Assert.Equal(project, root.GetProperty("labels").GetProperty("com.docker.compose.project").GetString());
        Assert.Equal(id, root.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.False(root.GetProperty("privileged").GetBoolean());
        var requested = root.GetProperty("requestedPorts").EnumerateObject().ToArray();
        Assert.Single(requested);
        Assert.Equal("5432/tcp", requested[0].Name);
        var requestedBinding = Assert.Single(requested[0].Value.EnumerateArray());
        Assert.Equal("127.0.0.1", requestedBinding.GetProperty("HostIp").GetString());
        // The empty create-request HostPort requests random allocation; validate
        // its actual nonempty assigned value through NetworkSettings.Ports.
        var ports = root.GetProperty("ports").EnumerateObject().ToArray();
        Assert.Single(ports);
        Assert.Equal("5432/tcp", ports[0].Name);
        var binding = Assert.Single(ports[0].Value.EnumerateArray());
        Assert.Equal("127.0.0.1", binding.GetProperty("HostIp").GetString());
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), binding.GetProperty("HostPort").GetString());
        var attached = root.GetProperty("networks").EnumerateObject().ToArray();
        Assert.Single(attached);
        Assert.Equal(network!.Name, attached[0].Name);
        if (AuthCatalogTransportRequested)
            Assert.Contains("postgres-" + id, attached[0].Value.GetProperty("Aliases")
                .EnumerateArray().Select(value => value.GetString()), StringComparer.Ordinal);
        var mount = Assert.Single(root.GetProperty("mounts").EnumerateArray());
        Assert.Equal("volume", mount.GetProperty("Type").GetString());
        Assert.Equal(volume!.Name, mount.GetProperty("Name").GetString());
        Assert.Equal("/var/lib/postgresql/data", mount.GetProperty("Destination").GetString());
        Assert.True(mount.GetProperty("RW").GetBoolean());
        var environment = root.GetProperty("env").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.True(environment.Contains("POSTGRES_PASSWORD_FILE=" + SecretPath, StringComparer.Ordinal));
        Assert.False(environment.Any(value => value.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal)));

        const string networkFormat = "{\"id\":{{json .Id}},\"name\":{{json .Name}},\"internal\":{{json .Internal}},\"driver\":{{json .Driver}},\"labels\":{{json .Labels}},\"containers\":{{json .Containers}}}";
        var actualNetwork = await DockerJsonAsync(["network", "inspect", "--format", networkFormat, network.Name], ct);
        Assert.Equal(attached[0].Value.GetProperty("NetworkID").GetString(), actualNetwork.GetProperty("id").GetString());
        Assert.Equal(network.Name, actualNetwork.GetProperty("name").GetString());
        Assert.False(actualNetwork.GetProperty("internal").GetBoolean());
        Assert.Equal("bridge", actualNetwork.GetProperty("driver").GetString());
        Assert.Equal(id, actualNetwork.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.Equal(container.Id, Assert.Single(actualNetwork.GetProperty("containers").EnumerateObject()).Name);
        const string volumeFormat = "{\"name\":{{json .Name}},\"driver\":{{json .Driver}},\"labels\":{{json .Labels}}}";
        var actualVolume = await DockerJsonAsync(["volume", "inspect", "--format", volumeFormat, volume.Name], ct);
        Assert.Equal(volume.Name, actualVolume.GetProperty("name").GetString());
        Assert.Equal("local", actualVolume.GetProperty("driver").GetString());
        Assert.Equal(id, actualVolume.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        // Raw Docker response/environment is never logged or included in receipts.
    }

    private static void RequireAuthCatalogLocalEngine()
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("AUTH_CATALOG_LOCAL_WINDOWS_ENGINE_REQUIRED");
        foreach (var name in new[] { "DOCKER_HOST", "DOCKER_CONTEXT", "TESTCONTAINERS_HOST_OVERRIDE",
            "TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE" })
            if (Environment.GetEnvironmentVariable(name) is not null)
                throw new InvalidOperationException("AUTH_CATALOG_ENGINE_OVERRIDE_REJECTED");
    }
    private async Task<JsonElement> DockerJsonAsync(string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("docker") { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true };
        if (AuthCatalogTransportRequested)
        {
            RequireAuthCatalogLocalEngine();
            start.ArgumentList.Add("--context");
            start.ArgumentList.Add("default");
        }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FIXTURE_INSPECT_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch { /* Preserve the first admission/deadline failure. */ }
            throw;
        }
        var raw = await stdout;
        var nativeError = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("FIXTURE_INSPECT_FAILED:" + process.ExitCode + ":" + SafeText(nativeError));
        using var parsed = JsonDocument.Parse(raw);
        return parsed.RootElement.Clone();
    }
    private InvalidOperationException SafeFailure(Exception error)
    {
        var capture = new SafePostgresDiagnosticCapture();
        foreach (var secret in new[] { adminSecret, migratorSecret, runtimeSecret }) capture.RegisterSecret(secret);
        capture.Capture(error);
        var detail = capture.Diagnostic is null ? SafeText(error.Message) : JsonSerializer.Serialize(capture.Diagnostic);
        return new InvalidOperationException("AUTH_ATOMIC_PROVIDER_FAILURE stage=" + stage + " category=" + error.GetType().Name + " detail=" + detail);
    }
    private string SafeText(string value)
    {
        foreach (var secret in new[] { adminSecret, migratorSecret, runtimeSecret }) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"(?i)(?:password|pwd)\s*=\s*[^;\r\n]*", "[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        return value.Length > 2048 ? value[..2048] + "[truncated]" : value;
    }
    private static string FreshSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static string Quote(string name) => new NpgsqlCommandBuilder().QuoteIdentifier(name);
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed class AuthOnboardingAtomicDatabase(IAuthMigrationDatabaseHost fixture, string name)
{
    private const string Previous = "20261018144506_BrowserSessionSnapshot";
    private const string Target = "20261115195218_OnboardingRegistration";
    private static readonly string[] Enrollment = ["EmailVerificationChallenges", "OnboardingGrants", "OnboardingOperationReceipts", "VerificationMailOutboxMessages"];
    private readonly string repository = FindRepository();
    internal string Name => name;

    internal async Task PrepareBaselineAsync(CancellationToken ct)
    {
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(repository, "Deployments/database/bootstrap-auth-roles.sql"), ct);
        await using (var admin = await fixture.OpenAdminAsync(name, ct))
        {
            await ExecuteAsync(admin, bootstrap, ct);
            await ExecuteAsync(admin, fixture.RolePasswordSql(), ct);
        }
        await using (var owner = await OpenOwnerAsync(ct))
        await using (var database = Context(owner))
        {
            AssertCompiledTargets(database);
            await database.GetService<IMigrator>().MigrateAsync(Previous, ct);
        }
        await using (var admin = await fixture.OpenAdminAsync(name, ct)) await ExecuteAsync(admin, bootstrap, ct);
        await AssertTargetAbsentAsync(ct);
    }

    // Compatibility reference only: this is the service fixture's explicit EF/bootstrap
    // sequence, not the canonical atomic executor and not an atomicity claim.
    internal async Task PrepareEfBootstrapCompatibilityAsync(CancellationToken ct)
    {
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(repository, "Deployments/database/bootstrap-auth-roles.sql"), ct);
        await using (var admin = await fixture.OpenAdminAsync(name, ct))
        {
            await ExecuteAsync(admin, bootstrap, ct);
            await ExecuteAsync(admin, fixture.RolePasswordSql(), ct);
        }
        await using (var owner = await OpenOwnerAsync(ct))
        await using (var database = Context(owner))
        {
            AssertCompiledTargets(database);
            await database.GetService<IMigrator>().MigrateAsync(Previous, ct);
            Assert.Equal(new[] { Target, AuthExactMigrationStep.OwnerTarget }, await database.Database.GetPendingMigrationsAsync(ct));
            await ExecuteAsync(owner, "SET ROLE zeka_auth_owner", ct);
            await database.GetService<IMigrator>().MigrateAsync(Target, ct);
            Assert.Equal(new[] { AuthExactMigrationStep.OwnerTarget }, await database.Database.GetPendingMigrationsAsync(ct));
        }
        await using (var admin = await fixture.OpenAdminAsync(name, ct)) await ExecuteAsync(admin, bootstrap, ct);
        await VerifyV20Async(ct);
    }

    private static void AssertCompiledTargets(AuthDbContext database)
    {
        var migrations = database.Database.GetMigrations().ToArray();
        Assert.True(migrations.Length >= 3);
        Assert.Equal(Previous, migrations[^3]);
        Assert.Equal(Target, migrations[^2]);
        Assert.Equal(AuthExactMigrationStep.OwnerTarget, migrations[^1]);
    }

    internal Task<EngineResult> ExecuteAsync(CancellationToken ct, AuthOnboardingHooks? hooks = null)
    {
        Assert.True(ServiceDescriptor.TryGet("auth", out var service));
        var options = new CommandOptions(MigrationOperation.Apply, service, Target, Guid.NewGuid(),
            Path.GetTempPath(), "provider-source-only.json", TimeSpan.FromSeconds(5));
        // This is the actual Git base, not a clean-commit claim. The dirty source
        // snapshot and built test/executor DLL hashes are bound in the external receipt.
        return AuthOnboardingMigrationExecutor.ExecuteAsync(options, fixture.MigratorConnection(name),
            "4806b71bfcfb168d53592692a935374981106af8", DateTimeOffset.UtcNow, ct, hooks);
    }

    internal async Task VerifyV20Async(CancellationToken ct)
    {
        await using var connection = await OpenOwnerAsync(ct);
        await using var database = Context(connection);
        await AuthExactMigrationStep.Load(Target).VerifyManifestAsync(database, ct);
    }

    internal Task<EngineResult> ExecuteOwnerIndexAsync(CancellationToken ct, AuthOnboardingHooks? hooks = null)
    {
        Assert.True(ServiceDescriptor.TryGet("auth", out var service));
        var options = new CommandOptions(MigrationOperation.Apply, service, AuthExactMigrationStep.OwnerTarget, Guid.NewGuid(),
            Path.GetTempPath(), "provider-owner-index.json", TimeSpan.FromSeconds(5));
        return AuthOnboardingMigrationExecutor.ExecuteAsync(options, fixture.MigratorConnection(name),
            "b158531ff28025b19846492436f805eb44426d2d", DateTimeOffset.UtcNow, ct, hooks);
    }

    internal async Task VerifyV21Async(CancellationToken ct)
    {
        await using var connection = await OpenOwnerAsync(ct);
        await using var database = Context(connection);
        await AuthExactMigrationStep.Load(AuthExactMigrationStep.OwnerTarget).VerifyManifestAsync(database, ct);
        Assert.True(await OrganisationOwnerIndexContract.IsInstalledAsync(database, ct));
    }

    internal async Task AssertTargetAbsentAsync(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAdminAsync(name, ct);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"=@target)=0
              AND (SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)=@previous
              AND NOT EXISTS(SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname='public' AND c.relname=ANY(@tables))
            """, connection);
        command.Parameters.AddWithValue("target", Target);
        command.Parameters.AddWithValue("previous", Previous);
        command.Parameters.AddWithValue("tables", Enrollment);
        Assert.True(await command.ExecuteScalarAsync(ct) is true, "Target/history/schema rollback was incomplete.");
    }

    internal async Task AssertCommittedAsync(CancellationToken ct)
    {
        await using (var connection = await fixture.OpenAdminAsync(name, ct))
        await using (var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM public."__EFMigrationsHistory" WHERE "MigrationId"=@target)=1
              AND (SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1)=@target
              AND (SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                  WHERE n.nspname='public' AND c.relname=ANY(@tables) AND c.relkind='r')=4
            """, connection))
        {
            command.Parameters.AddWithValue("target", Target);
            command.Parameters.AddWithValue("tables", Enrollment);
            Assert.True(await command.ExecuteScalarAsync(ct) is true, "Target history/schema was not committed exactly once.");
        }
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnection(name));
        await runtime.OpenAsync(ct);
        await using var grants = new NpgsqlCommand("""
            SELECT bool_and(pg_catalog.has_table_privilege(current_user,c.oid,'SELECT')
                AND pg_catalog.has_table_privilege(current_user,c.oid,'INSERT')
                AND NOT pg_catalog.has_table_privilege(current_user,c.oid,'UPDATE')
                AND NOT pg_catalog.has_table_privilege(current_user,c.oid,'DELETE')) AND count(*)=4
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public' AND c.relname=ANY(@tables)
            """, runtime);
        grants.Parameters.AddWithValue("tables", Enrollment);
        Assert.True(await grants.ExecuteScalarAsync(ct) is true, "Actual runtime table grants diverged from exact source contract.");
    }

    internal async Task AddUnexpectedDefaultGrantAsync(CancellationToken ct)
    {
        await using var connection = await OpenOwnerAsync(ct);
        await ExecuteAsync(connection, "ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO zeka_auth_runtime", ct);
    }

    internal async Task AssertSameAdvisoryLockWaiterAsync(int holderPid, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(TimeSpan.FromSeconds(3));
        await using var connection = await fixture.OpenAdminAsync(name, bound.Token);
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(
                  SELECT 1 FROM pg_catalog.pg_locks held JOIN pg_catalog.pg_locks waiting
                    ON waiting.locktype=held.locktype AND waiting.database=held.database
                    AND waiting.classid=held.classid AND waiting.objid=held.objid AND waiting.objsubid=held.objsubid
                  WHERE held.pid=@holder AND held.locktype='advisory' AND held.granted
                    AND waiting.pid<>held.pid AND NOT waiting.granted)
                """, connection) { CommandTimeout = 2 };
            command.Parameters.AddWithValue("holder", holderPid);
            if (await command.ExecuteScalarAsync(bound.Token) is true) return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), bound.Token);
        }
    }

    internal async Task<string> DigestAsync(CancellationToken ct) => (await CaptureCatalogAsync(ct)).Sha256;

    // Only schema/security metadata is read. Credentials, role password hashes and
    // application rows are excluded. Principal/object OIDs become names; the current
    // disposable database name becomes <current>. No ACL, role or policy is filtered out.
    internal async Task<AuthCatalogSnapshot> CaptureCatalogAsync(CancellationToken ct)
    {
        await using var connection = await fixture.OpenAdminAsync(name, ct);
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
        // Canonical query bytes are LF on both Windows and Linux; result values are not rewritten.
        command.CommandText = command.CommandText.Replace("\r\n", "\n", StringComparison.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            Assert.True(rows.Count < 20000, "Catalog capture exceeded the reviewed bound.");
            var row = reader.GetString(0);
            Assert.True(row.Length <= 65536, "Catalog row exceeded the reviewed bound.");
            rows.Add(row);
        }
        return new AuthCatalogSnapshot("zeka-auth-catalog/v1",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText))).ToLowerInvariant(),
            rows.OrderBy(row => row, StringComparer.Ordinal).ToArray(),
            AuthCatalogAssemblyIdentity.Read(typeof(DbContext).Assembly),
            AuthCatalogAssemblyIdentity.Read(typeof(AuthDbContext).Assembly),
            AuthCatalogAssemblyIdentity.Read(typeof(AuthOnboardingAtomicFixture).Assembly), DateTimeOffset.UtcNow);
    }

    internal async Task AssertRuntimeDenialsAsync(CancellationToken ct)
    {
        foreach (var sql in new[]
        {
            "CREATE SCHEMA atomic_runtime_denied",
            "CREATE TABLE public.atomic_runtime_denied(id integer)",
            "CREATE TABLE zeka.atomic_runtime_denied(id integer)",
            "UPDATE public.\"EmailVerificationChallenges\" SET \"TokenHash\"=\"TokenHash\"",
            "DELETE FROM public.\"EmailVerificationChallenges\"",
            "UPDATE public.\"OnboardingGrants\" SET \"CredentialHash\"=\"CredentialHash\"",
            "UPDATE public.\"OnboardingOperationReceipts\" SET \"Generation\"=\"Generation\""
        }) await AssertDeniedAsync(fixture.RuntimeConnection(name), sql, ct);
        foreach (var sql in new[] { "CREATE SCHEMA atomic_migrator_denied", "CREATE TABLE public.atomic_migrator_denied(id integer)" })
            await AssertDeniedAsync(fixture.MigratorConnection(name), sql, ct);
    }

    private static async Task AssertDeniedAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(ct);
        });
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
    }

    private async Task<NpgsqlConnection> OpenOwnerAsync(CancellationToken ct)
    {
        var connection = new NpgsqlConnection(fixture.MigratorConnection(name));
        try
        {
            await connection.OpenAsync(ct);
            await ExecuteAsync(connection, "SET ROLE zeka_auth_owner", ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    private static AuthDbContext Context(NpgsqlConnection connection) => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deployments/database/bootstrap-auth-roles.sql"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("SOURCE_BOOTSTRAP_NOT_FOUND");
    }
}

internal sealed record AuthCatalogSnapshot(string SchemaVersion, string QuerySha256, string[] Rows,
    AuthCatalogAssemblyIdentity EntityFramework, AuthCatalogAssemblyIdentity AuthInfrastructure,
    AuthCatalogAssemblyIdentity ProducerAssembly, DateTimeOffset CapturedAtUtc)
{
    internal string Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Rows))));
}

internal sealed record AuthCatalogAssemblyIdentity(string Name, string Version, string InformationalVersion,
    string ModuleVersionId, string Sha256)
{
    internal static AuthCatalogAssemblyIdentity Read(Assembly assembly) => new(
        assembly.GetName().Name ?? throw new InvalidOperationException("CATALOG_ASSEMBLY_IDENTITY_REQUIRED"),
        assembly.GetName().Version?.ToString() ?? string.Empty,
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty,
        assembly.ManifestModule.ModuleVersionId.ToString("D"),
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant());
}
