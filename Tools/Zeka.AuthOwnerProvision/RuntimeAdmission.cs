using System.Text.Json;
using Npgsql;
using Zeka.PersistenceSecurity;

namespace Zeka.AuthOwnerProvision;

internal static class RuntimeAdmission
{
    internal static async Task ValidateAsync(AdmittedOwnerInput admitted, CancellationToken cancellationToken)
    {
        var input = admitted.Value;
        using var individual = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        individual.CancelAfter(TimeSpan.FromSeconds(30));
        await new RuntimeDatabaseIdentityValidator(
            input.AuthRuntimeConnectionString, "zeka_auth_runtime").StartAsync(individual.Token);
        await using var connection = new NpgsqlConnection(input.AuthRuntimeConnectionString);
        await connection.OpenAsync(individual.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = """
            select current_database(),session_user,current_user,
              pg_catalog.shobj_description(r.oid,'pg_authid'),
              pg_catalog.shobj_description(d.oid,'pg_database')
            from pg_catalog.pg_roles r cross join pg_catalog.pg_database d
            where r.rolname=current_user and d.datname=current_database()
            """;
        await using var reader = await command.ExecuteReaderAsync(individual.Token);
        if (!await reader.ReadAsync(individual.Token) || reader.GetString(0) != input.DatabaseName
            || reader.GetString(1) != "zeka_auth_runtime" || reader.GetString(2) != "zeka_auth_runtime"
            || reader.IsDBNull(3) || reader.IsDBNull(4))
            throw new OwnerToolException("RUNTIME_INITIALIZATION_REJECTED");
        ValidateMarkers(input, reader.GetString(3), reader.GetString(4));
        if (await reader.ReadAsync(individual.Token))
            throw new OwnerToolException("RUNTIME_INITIALIZATION_REJECTED");
    }

    internal static void ValidateMarkers(OwnerInput input, string roleComment, string databaseComment)
    {
        try
        {
            using var document = JsonDocument.Parse(roleComment);
            var root = document.RootElement;
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 5 || properties.Select(value => value.Name)
                    .Distinct(StringComparer.Ordinal).Count() != 5
                || root.GetProperty("schema").GetString() != "zeka-m1-role/v1"
                || root.GetProperty("initializationId").GetString() != input.InitializationId
                || root.GetProperty("service").GetString() != "auth"
                || root.GetProperty("kind").GetString() != "runtime"
                || root.GetProperty("sourceCommit").GetString() != input.DatabaseSourceCommit
                || databaseComment != $"zeka-m1-database/v1:{input.InitializationId}:auth:{input.DatabaseSourceCommit}")
                throw new OwnerToolException("RUNTIME_INITIALIZATION_REJECTED");
        }
        catch (OwnerToolException) { throw; }
        catch { throw new OwnerToolException("RUNTIME_INITIALIZATION_REJECTED"); }
    }
}
