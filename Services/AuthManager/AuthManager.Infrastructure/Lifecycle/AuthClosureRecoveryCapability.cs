using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Lifecycle;

public interface IAuthClosureRecoveryCapability
{
    Task ReleaseAsync(ReleaseOrganisationClosureFenceV1 command, DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default);
}

public sealed class NpgsqlAuthClosureRecoveryCapability(string connectionString)
    : IAuthClosureRecoveryCapability
{
    public async Task ReleaseAsync(ReleaseOrganisationClosureFenceV1 command, DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default)
    {
        try { await ReleaseCoreAsync(command, releasedAt, cancellationToken); }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException("Auth closure recovery was rejected by PostgreSQL.", exception);
        }
    }

    private async Task ReleaseCoreAsync(ReleaseOrganisationClosureFenceV1 command,
        DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await DemandRecoveryIdentityAsync(connection, transaction, cancellationToken);
        await using (var tenant = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            tenant.Parameters.AddWithValue("organisation", command.Header.OrganisationId.ToString("D"));
            await tenant.ExecuteScalarAsync(cancellationToken);
        }
        await using var release = new NpgsqlCommand("""
            SELECT zeka.release_auth_closure_fence(
              @operation, @organisation, @revision, @token, @causation, @correlation, @released_at)
            """, connection, transaction);
        release.Parameters.AddWithValue("operation", command.Header.OperationId);
        release.Parameters.AddWithValue("organisation", command.Header.OrganisationId);
        release.Parameters.AddWithValue("revision", command.Header.OperationRevision);
        release.Parameters.AddWithValue("token", command.FenceToken);
        release.Parameters.AddWithValue("causation", command.Header.CausationId);
        release.Parameters.AddWithValue("correlation", command.Header.CorrelationId);
        release.Parameters.AddWithValue("released_at", releasedAt);
        if (await release.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("Auth closure recovery capability rejected the release.");
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task DemandRecoveryIdentityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT role.rolsuper
              AND current_user NOT IN ('zeka_auth_runtime', 'zeka_auth_owner')
              AND pg_catalog.has_function_privilege(current_user,
                'zeka.release_auth_closure_fence(uuid,uuid,bigint,text,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
            FROM pg_catalog.pg_roles role WHERE role.rolname=current_user
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("A distinct least-privilege Auth recovery identity is required.");
    }
}

internal sealed class UnavailableAuthClosureRecoveryCapability : IAuthClosureRecoveryCapability
{
    public Task ReleaseAsync(ReleaseOrganisationClosureFenceV1 command, DateTimeOffset releasedAt,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Auth closure recovery is not configured.");
}
