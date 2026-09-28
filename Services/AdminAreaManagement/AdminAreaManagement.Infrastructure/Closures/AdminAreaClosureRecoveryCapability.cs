using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace AdminAreaManagement.Infrastructure.Closures;

public interface IAdminAreaClosureRecoveryCapability
{
    Task<DateTimeOffset> ReleaseAsync(ReleaseOrganisationClosureFenceV1 command,
        DateTimeOffset releasedAt, CancellationToken cancellationToken = default);
}

public sealed class NpgsqlAdminAreaClosureRecoveryCapability(string connectionString)
    : IAdminAreaClosureRecoveryCapability
{
    public async Task<DateTimeOffset> ReleaseAsync(ReleaseOrganisationClosureFenceV1 command,
        DateTimeOffset releasedAt, CancellationToken cancellationToken = default)
    {
        try { return await ReleaseCoreAsync(command, releasedAt, cancellationToken); }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException("AdminArea closure recovery was rejected by PostgreSQL.", exception);
        }
    }

    private async Task<DateTimeOffset> ReleaseCoreAsync(ReleaseOrganisationClosureFenceV1 command,
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
            SELECT zeka.release_adminarea_closure_fence(
              @operation,@organisation,@participant,@revision,@token,@contract,
              @message,@causation,@correlation,@released_at)
            """, connection, transaction);
        release.Parameters.AddWithValue("operation", command.Header.OperationId);
        release.Parameters.AddWithValue("organisation", command.Header.OrganisationId);
        release.Parameters.AddWithValue("participant", command.Header.ParticipantId);
        release.Parameters.AddWithValue("revision", command.Header.OperationRevision);
        release.Parameters.AddWithValue("token", command.FenceToken);
        release.Parameters.AddWithValue("contract", command.Header.ContractVersion);
        release.Parameters.AddWithValue("message", command.Header.MessageId);
        release.Parameters.AddWithValue("causation", command.Header.CausationId);
        release.Parameters.AddWithValue("correlation", command.Header.CorrelationId);
        release.Parameters.AddWithValue("released_at", releasedAt);
        var value = await release.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("AdminArea closure recovery returned no release time.");
        await transaction.CommitAsync(cancellationToken);
        return value is DateTimeOffset offset
            ? offset
            : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
    }

    private static async Task DemandRecoveryIdentityAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT role.rolsuper
              AND current_user NOT IN ('zeka_adminarea_runtime', 'zeka_adminarea_owner')
              AND pg_catalog.has_function_privilege(current_user,
                'zeka.release_adminarea_closure_fence(uuid,uuid,text,bigint,text,integer,uuid,uuid,uuid,timestamp with time zone)',
                'EXECUTE')
            FROM pg_catalog.pg_roles role WHERE role.rolname=current_user
            """, connection, transaction);
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("A distinct least-privilege AdminArea recovery identity is required.");
    }
}
