using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Volumes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Zeka.DbMigrate.Tests;

internal interface IAuthMigrationDatabaseHost
{
    Task<NpgsqlConnection> OpenAdminAsync(string database, CancellationToken ct);
    string MigratorConnection(string database);
    string RuntimeConnection(string database);
    string RolePasswordSql();
}

// Separate ordinary provider fixture. It does not claim the older dedicated-bridge
// transport qualification, and does not alter that fixture's guards or retained lifecycle.
public sealed class AuthOwnerIndexPostgreSqlFixture : IAsyncLifetime, IAuthMigrationDatabaseHost
{
    private const string Image = "postgres@sha256:18cfe3ef5e6815560c98237d6216d1e5119702fb0f3894c8785dd58b8bbe5d73";
    private const string Engine = "0b62f5ec-15d9-4e4b-a450-817d52378eef";
    private const string SecretPath = "/run/secrets/admin-password";
    private readonly string id = Guid.NewGuid().ToString("N");
    private readonly string adminSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string migratorSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string runtimeSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private IContainer? container;
    private IVolume? volume;
    private ushort port;
    private bool createdVolume;
    private string? observedImage;
    internal string ContainerId => container?.Id ?? throw new InvalidOperationException("OWNED_CONTAINER_NOT_CREATED");
    internal string ImageIdentity => observedImage ?? Image;
    internal string VolumeName => "zeka-owner-index-tests-" + id + "-data";

    public async Task InitializeAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = budget.Token;
        if (OperatingSystem.IsWindows())
        {
            var endpoint = Environment.GetEnvironmentVariable("DOCKER_HOST");
            Assert.True(endpoint is null or "" or "npipe://./pipe/docker_engine" or "npipe:////./pipe/docker_engine", "Docker endpoint is not the admitted owner-local pipe.");
            var context = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
            Assert.True(context is null or "" or "default", "Docker context is not admitted.");
            var engine = await DockerAsync(["info", "--format", "{{.ID}}"], ct);
            Assert.Equal(0, engine.Exit);
            Assert.Equal(Engine, engine.Output.Trim());
        }
        var image = await DockerAsync(["image", "inspect", Image, "--format", "{{.Id}}"], ct);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(0, image.Exit); // No implicit local pull or image substitution.
            observedImage = image.Output.Trim();
            Assert.StartsWith("sha256:", observedImage);
        }
        var labels = new Dictionary<string, string>
        {
            ["com.diliwo.zeka.fixture-id"] = id,
            ["com.diliwo.zeka.owner"] = "codex",
            ["com.diliwo.zeka.scope"] = "organisation-owner-index-tests"
        };
        volume = new VolumeBuilder().WithName(VolumeName).WithLabel(labels).WithCleanUp(false).Build();
        await volume.CreateAsync(ct);
        createdVolume = true;
        var secret = Encoding.UTF8.GetBytes(adminSecret);
        try
        {
            var builder = new ContainerBuilder(Image)
                .WithName("zeka-owner-index-tests-" + id + "-postgres").WithLabel(labels)
                .WithCleanUp(false).WithAutoRemove(true).WithLogger(NullLogger.Instance)
                .WithEnvironment("POSTGRES_USER", "postgres")
                .WithEnvironment("POSTGRES_PASSWORD_FILE", SecretPath)
                .WithEnvironment("POSTGRES_INITDB_ARGS", "--auth-host=scram-sha-256")
                .WithResourceMapping(secret, SecretPath, uid: 70, gid: 70, fileMode: UnixFileModes.UserRead)
                .WithVolumeMount(volume, "/var/lib/postgresql/data", AccessMode.ReadWrite)
                .WithPortBinding(5432, true)
                .WithCreateParameterModifier(create =>
                {
                    var ports = create.HostConfig?.PortBindings ?? throw new InvalidOperationException("OWNER_INDEX_PORT_BINDING_REJECTED");
                    if (ports.Count != 1) throw new InvalidOperationException("OWNER_INDEX_PORT_BINDING_REJECTED");
                    foreach (var binding in ports.Values.SelectMany(value => value))
                        binding.HostIP = "127.0.0.1";
                })
                .WithCommand("-c", "log_statement=none", "-c", "log_min_error_statement=panic")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted(
                    ["pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "postgres"]));
            if (OperatingSystem.IsWindows()) builder = builder.WithImagePullPolicy(_ => false);
            container = builder.Build();
            await container.StartAsync(ct);
            port = container.GetMappedPublicPort(5432);
            await VerifyOwnedContainerAsync(ct);
            var mode = await container.ExecAsync(["stat", "-c", "%u:%g:%a", SecretPath], ct);
            Assert.Equal(0, mode.ExitCode);
            Assert.Equal("70:70:400", mode.Stdout.Trim());
            await using var connection = await OpenAdminAsync("postgres", ct);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('server_version_num')::integer/10000=17
                  AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on'
                  AND current_setting('synchronous_commit')='on'
                  AND session_user='postgres' AND current_user='postgres' AND current_database()='postgres'
                  AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname NOT IN ('postgres','template0','template1'))
                """, connection);
            Assert.True(await command.ExecuteScalarAsync(ct) is true);
        }
        catch
        {
            await DisposeAsync(); // Only resources created by this fresh fixture are eligible.
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    internal async Task<AuthOnboardingAtomicDatabase> NewDatabaseAsync(CancellationToken ct)
    {
        var name = "owner_index_" + Guid.NewGuid().ToString("N");
        await using (var connection = await OpenAdminAsync("postgres", ct))
        await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", connection))
            await command.ExecuteNonQueryAsync(ct);
        return new AuthOnboardingAtomicDatabase(this, name);
    }

    internal async Task<NpgsqlConnection> OpenOwnerAsync(string database, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(MigratorConnection(database));
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SET ROLE zeka_auth_owner", connection);
        await command.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task<NpgsqlConnection> OpenAdminAsync(string database, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(Connection(database, "postgres", adminSecret));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public string MigratorConnection(string database) => Connection(database, "zeka_auth_migrator", migratorSecret);
    public string RuntimeConnection(string database) => Connection(database, "zeka_auth_runtime", runtimeSecret);
    public string RolePasswordSql() => "ALTER ROLE zeka_auth_migrator PASSWORD " + Literal(migratorSecret)
        + "; ALTER ROLE zeka_auth_runtime PASSWORD " + Literal(runtimeSecret) + ";";

    private string Connection(string database, string role, string secret) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = port, Database = database, Username = role, Password = secret,
        Pooling = false, Timeout = 5, CommandTimeout = 30, IncludeErrorDetail = false
    }.ConnectionString;
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private async Task VerifyOwnedContainerAsync(CancellationToken ct)
    {
        const string format = """{"id":{{json .Id}},"image":{{json .Image}},"labels":{{json .Config.Labels}},"ports":{{json .NetworkSettings.Ports}},"networks":{{json .NetworkSettings.Networks}},"mounts":{{json .Mounts}},"privileged":{{json .HostConfig.Privileged}},"env":{{json .Config.Env}}}""";
        var observation = await DockerAsync(["container", "inspect", "--format", format, ContainerId], ct);
        Assert.Equal(0, observation.Exit);
        using var document = JsonDocument.Parse(observation.Output);
        var root = document.RootElement;
        Assert.Equal(ContainerId, root.GetProperty("id").GetString());
        Assert.Equal(id, root.GetProperty("labels").GetProperty("com.diliwo.zeka.fixture-id").GetString());
        Assert.Equal("organisation-owner-index-tests", root.GetProperty("labels").GetProperty("com.diliwo.zeka.scope").GetString());
        Assert.False(root.GetProperty("privileged").GetBoolean());
        Assert.Equal("bridge", Assert.Single(root.GetProperty("networks").EnumerateObject()).Name);
        var mount = Assert.Single(root.GetProperty("mounts").EnumerateArray());
        Assert.Equal("volume", mount.GetProperty("Type").GetString());
        Assert.Equal(VolumeName, mount.GetProperty("Name").GetString());
        Assert.Equal("/var/lib/postgresql/data", mount.GetProperty("Destination").GetString());
        var portBinding = Assert.Single(root.GetProperty("ports").GetProperty("5432/tcp").EnumerateArray());
        Assert.Equal("127.0.0.1", portBinding.GetProperty("HostIp").GetString());
        Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), portBinding.GetProperty("HostPort").GetString());
        var environment = root.GetProperty("env").EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.True(environment.Contains("POSTGRES_PASSWORD_FILE=" + SecretPath, StringComparer.Ordinal), "Protected password-file delivery is missing.");
        Assert.False(environment.Any(value => value.StartsWith("POSTGRES_PASSWORD=", StringComparison.Ordinal)), "Credential delivery is not file-only.");
        if (observedImage is not null) Assert.Equal(observedImage, root.GetProperty("image").GetString());
    }

    public async Task DisposeAsync()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = budget.Token;
        if (container?.Id is { Length: > 0 } containerId)
        {
            await VerifyOwnedContainerAsync(ct);
            await container.DisposeAsync();
            await RequireAbsentAsync("container", containerId, ct);
            container = null;
        }
        if (createdVolume && volume is not null)
        {
            var check = await DockerAsync(["volume", "inspect", VolumeName, "--format", """{{index .Labels "com.diliwo.zeka.fixture-id"}}"""], ct);
            Assert.Equal(0, check.Exit);
            Assert.Equal(id, check.Output.Trim());
            await volume.DeleteAsync(ct);
            await RequireAbsentAsync("volume", VolumeName, ct);
            createdVolume = false;
        }
    }

    private static async Task RequireAbsentAsync(string kind, string identity, CancellationToken ct)
    {
        var result = await DockerAsync([kind, "inspect", identity], ct);
        Assert.NotEqual(0, result.Exit);
        Assert.Contains("no such", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(identity, result.Error, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Output, string Error)> DockerAsync(string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput=true, RedirectStandardError=true, UseShellExecute=false };
        if (OperatingSystem.IsWindows()) { info.ArgumentList.Add("--context"); info.ArgumentList.Add("default"); }
        foreach (var value in arguments) info.ArgumentList.Add(value);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("DOCKER_PROCESS_NOT_STARTED");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await output, await error);
    }
}
