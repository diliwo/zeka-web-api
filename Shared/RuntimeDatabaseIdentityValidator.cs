using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Zeka.PersistenceSecurity;

/// <summary>Fails service startup when the configured database credential is not the restricted runtime role.</summary>
public sealed class RuntimeDatabaseIdentityValidator(string connectionString, string expectedRole) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            select r.rolname,r.rolcanlogin,r.rolsuper,r.rolbypassrls,r.rolcreatedb,r.rolcreaterole,
                   r.rolinherit,r.rolreplication,r.rolconfig is null,
                   not exists(select 1 from pg_auth_members m where m.member=r.oid or m.roleid=r.oid),
                   not exists(select 1 from pg_db_role_setting s where s.setrole=r.oid)
            from pg_roles r where r.rolname=current_user
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != expectedRole
            || !reader.GetBoolean(1) || reader.GetBoolean(2) || reader.GetBoolean(3)
            || reader.GetBoolean(4) || reader.GetBoolean(5) || reader.GetBoolean(6)
            || reader.GetBoolean(7) || !reader.GetBoolean(8) || !reader.GetBoolean(9)
            || !reader.GetBoolean(10) || await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The configured database credential is not the approved restricted runtime identity.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
