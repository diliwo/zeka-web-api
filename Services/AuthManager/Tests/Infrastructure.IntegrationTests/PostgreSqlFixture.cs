using Npgsql;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

[CollectionDefinition(CollectionName)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string CollectionName = "AuthManager PostgreSQL";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var databaseName = $"auth_tests_{Guid.NewGuid():N}";

        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = databaseName
        };

        return connectionString.ConnectionString;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
