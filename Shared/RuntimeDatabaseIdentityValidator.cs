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
            select session_user,current_user,
                   r.rolcanlogin,r.rolsuper,r.rolbypassrls,r.rolcreatedb,r.rolcreaterole,
                   r.rolinherit,r.rolreplication,r.rolconfig is null,
                   not exists(select 1 from pg_catalog.pg_auth_members m where m.member=r.oid or m.roleid=r.oid),
                   not exists(select 1 from pg_catalog.pg_db_role_setting s where s.setrole=r.oid),
                   not exists(
                     select 1 from pg_catalog.pg_parameter_acl parameter
                     where pg_catalog.has_parameter_privilege(current_user,parameter.parname,'SET')
                        or pg_catalog.has_parameter_privilege(current_user,parameter.parname,'ALTER SYSTEM'))
            from pg_catalog.pg_roles r where r.rolname=current_user
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)
            || reader.GetString(0) != expectedRole || reader.GetString(1) != expectedRole
            || !reader.GetBoolean(2) || reader.GetBoolean(3) || reader.GetBoolean(4)
            || reader.GetBoolean(5) || reader.GetBoolean(6) || reader.GetBoolean(7)
            || reader.GetBoolean(8) || !reader.GetBoolean(9) || !reader.GetBoolean(10)
            || !reader.GetBoolean(11) || !reader.GetBoolean(12)
            || await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The configured database credential is not the approved restricted runtime identity.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
