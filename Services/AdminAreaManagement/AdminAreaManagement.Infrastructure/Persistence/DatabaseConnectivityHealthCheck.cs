using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AdminAreaManagement.Infrastructure.Persistence;

/// <summary>Non-tenant connectivity probe; it never uses the guarded runtime DbContext.</summary>
internal sealed class DatabaseConnectivityHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("select 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Database connectivity probe failed.", exception);
        }
    }
}
