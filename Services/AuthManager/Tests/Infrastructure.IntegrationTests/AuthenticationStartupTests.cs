using System.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Infrastructure.IntegrationTests;

public sealed class AuthenticationStartupTests : Zeka.Authentication.Tests.RealHostStartupTests, IAsyncLifetime
{
    private const string RuntimePassword = "startup-runtime-password";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    protected override string ApiDirectory => "Services/AuthManager/AuthManager.API";
    protected override string AssemblyName => "AuthManager.API";
    protected override bool IsIssuer => true;

    public async Task InitializeAsync()
    {
        await postgres.StartAsync();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(ReadBootstrapScript(), connection);
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}'";
        await command.ExecuteNonQueryAsync();
    }

    protected override Task PrepareHostAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        start.Environment["ConnectionStrings__Default"] = new NpgsqlConnectionStringBuilder(
            postgres.GetConnectionString())
        {
            Username = "zeka_auth_runtime",
            Password = RuntimePassword
        }.ConnectionString;
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    private static string ReadBootstrapScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed Auth role bootstrap script was not found.");
    }
}
