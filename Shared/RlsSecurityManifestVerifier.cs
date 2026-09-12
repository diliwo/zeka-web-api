using System.Data.Common;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Zeka.PersistenceSecurity;

public sealed record RlsSecurityManifest(int SchemaVersion, string Service, string ModelContext,
    string MigrationId, string OwnerRole, string MigratorRole, string RuntimeRole,
    string[] ProtectedTables, string[] ExcludedTables, string[] Sequences,
    string[] RuntimeFunctions, string PolicyPrefix, string? ContextFunction);

/// <summary>Deployment-only bidirectional comparison of the versioned model and effective PostgreSQL state.</summary>
public static class RlsSecurityManifestVerifier
{
    private static readonly string[] Dml = ["DELETE", "INSERT", "SELECT", "UPDATE"];

    public static RlsSecurityManifest Load(Assembly assembly)
    {
        var name = assembly.GetManifestResourceNames().Single(x =>
            x.EndsWith("rls-manifest.v1.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("RLS manifest resource is missing.");
        return JsonSerializer.Deserialize<RlsSecurityManifest>(stream, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("RLS manifest is malformed.");
    }

    public static IReadOnlyCollection<string> GenerateProtectedInventory(DbContext database) => database.Model
        .GetEntityTypes().Where(type => IsTenantOwned(type.ClrType))
        .Select(Table).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    public static async Task VerifyAsync(DbContext database, Assembly manifestAssembly,
        CancellationToken cancellationToken = default)
    {
        var manifest = Load(manifestAssembly);
        if (manifest.SchemaVersion != 1 || database.GetType().FullName != manifest.ModelContext)
            throw new InvalidOperationException("Manifest identity does not match the deployment model.");
        if (manifest.ProtectedTables.Intersect(manifest.ExcludedTables, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("Protected and excluded inventories overlap.");
        var mapped = database.Model.GetEntityTypes().Select(Table).Distinct(StringComparer.Ordinal).ToArray();
        Equal(manifest.ProtectedTables, GenerateProtectedInventory(database), "EF protected inventory");
        Equal(manifest.ProtectedTables.Concat(manifest.ExcludedTables), mapped, "EF complete classification");
        foreach (var type in database.Model.GetEntityTypes().Where(type =>
                     manifest.ProtectedTables.Contains(Table(type), StringComparer.Ordinal)))
            if (type.FindProperty("OrganisationId")?.ClrType != typeof(Guid)
                || !IsTenantOwned(type.ClrType))
                throw new InvalidOperationException("A protected mapping lacks tenant ownership or UUID OrganisationId.");

        var connection = database.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        try
        {
            await VerifyRoles(connection, manifest, cancellationToken);
            await VerifyTablesAndPolicies(connection, manifest, cancellationToken);
            await VerifyDatabaseAndSchemas(connection, manifest, cancellationToken);
            await VerifyTableAcls(connection, manifest, cancellationToken);
            await VerifySequences(connection, manifest, cancellationToken);
            await VerifyFunctions(connection, manifest, cancellationToken);
            await VerifyDefaultPrivileges(connection, manifest, cancellationToken);
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static async Task VerifyRoles(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var managed = new[] { manifest.OwnerRole, manifest.MigratorRole, manifest.RuntimeRole };
        var roles = await RowsAsync(connection, """
            select rolname,rolcanlogin::text,rolsuper::text,rolbypassrls::text,rolcreatedb::text,
                   rolcreaterole::text,rolinherit::text,rolreplication::text,rolconnlimit::text,
                   coalesce(array_to_string(rolconfig,','),'')
            from pg_roles where rolname=any(@roles) order by rolname
            """, cancellationToken, ("roles", managed));
        Equal(managed, roles.Select(row => row[0]), "managed roles");
        AssertRole(roles, manifest.OwnerRole, false);
        AssertRole(roles, manifest.MigratorRole, true);
        AssertRole(roles, manifest.RuntimeRole, true);
        var memberships = await RowsAsync(connection, """
            select member.rolname,parent.rolname,m.admin_option::text,m.inherit_option::text,m.set_option::text
            from pg_auth_members m join pg_roles parent on parent.oid=m.roleid join pg_roles member on member.oid=m.member
            where parent.rolname=any(@roles) or member.rolname=any(@roles) order by member.rolname,parent.rolname
            """, cancellationToken, ("roles", managed));
        if (memberships.Count != 1 || memberships[0][0] != manifest.MigratorRole
            || memberships[0][1] != manifest.OwnerRole || memberships[0][2] != "false"
            || memberships[0][3] != "false" || memberships[0][4] != "true")
            throw new InvalidOperationException("Role membership state drifted.");
        var settings = await RowsAsync(connection, """
            select role.rolname from pg_db_role_setting setting join pg_roles role on role.oid=setting.setrole
            where role.rolname=any(@roles)
            """, cancellationToken, ("roles", managed));
        if (settings.Count != 0) throw new InvalidOperationException("Managed role/database settings drifted.");
    }

    private static async Task VerifyTablesAndPolicies(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var tables = await RowsAsync(connection, """
            select c.relname,owner.rolname,c.relrowsecurity::text,c.relforcerowsecurity::text
            from pg_class c join pg_namespace n on n.oid=c.relnamespace join pg_roles owner on owner.oid=c.relowner
            where n.nspname='public' and c.relkind in ('r','p') and c.relname<>'__EFMigrationsHistory' order by c.relname
            """, cancellationToken);
        Equal(manifest.ProtectedTables.Concat(manifest.ExcludedTables), tables.Select(row => row[0]), "catalog tables");
        foreach (var row in tables)
        {
            var tenant = manifest.ProtectedTables.Contains(row[0], StringComparer.Ordinal);
            if (row[1] != manifest.OwnerRole || row[2] != tenant.ToString().ToLowerInvariant()
                || row[3] != tenant.ToString().ToLowerInvariant())
                throw new InvalidOperationException("Table ownership or RLS state drifted.");
        }
        var history = await RowsAsync(connection, """
            select owner.rolname,
              (select h."MigrationId" from "__EFMigrationsHistory" h order by h."MigrationId" desc limit 1)
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            join pg_roles owner on owner.oid=c.relowner
            where n.nspname='public' and c.relname='__EFMigrationsHistory' and c.relkind='r'
            """, cancellationToken);
        if (history.Count != 1 || history[0][0] != manifest.OwnerRole
            || history[0][1] != manifest.MigrationId)
            throw new InvalidOperationException("Migration-history identity or ownership drifted.");
        var policies = await RowsAsync(connection, """
            select c.relname,p.polname,p.polcmd::text,p.polpermissive::text,
                   pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid),
                   array_to_string(array(select rolname from pg_roles where oid=any(p.polroles) order by rolname),',')
            from pg_policy p join pg_class c on c.oid=p.polrelid join pg_namespace n on n.oid=c.relnamespace
            where n.nspname='public' order by c.relname,p.polname
            """, cancellationToken);
        Equal(manifest.ProtectedTables, policies.Select(row => row[0]), "RLS policy targets");
        var expression = Normalize("\"OrganisationId\" = zeka.current_organisation_id()");
        foreach (var row in policies)
            if (row[1] != manifest.PolicyPrefix + row[0].ToLowerInvariant() + "_organisation"
                || row[2] != "*" || row[3] != "true" || row[6] != manifest.RuntimeRole
                || Normalize(row[4]) != expression || Normalize(row[5]) != expression)
                throw new InvalidOperationException("RLS policy definition drifted.");
    }

    private static async Task VerifyDatabaseAndSchemas(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var schemas = manifest.ContextFunction is null ? new[] { "public" } : new[] { "public", "zeka" };
        var owners = await RowsAsync(connection, """
            select n.nspname,owner.rolname from pg_namespace n join pg_roles owner on owner.oid=n.nspowner
            where n.nspname=any(@schemas) order by n.nspname
            """, cancellationToken, ("schemas", schemas));
        Equal(schemas, owners.Select(row => row[0]), "schema inventory");
        if (owners.Any(row => row[1] != manifest.OwnerRole)) throw new InvalidOperationException("Schema owner drifted.");
        var databaseAcls = await RowsAsync(connection, """
            select coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_database d cross join lateral aclexplode(coalesce(d.datacl,acldefault('d',d.datdba))) x
            left join pg_roles grantee on grantee.oid=x.grantee left join pg_roles owner on owner.oid=d.datdba
            where d.datname=current_database() and x.grantee<>d.datdba order by 1,2
            """, cancellationToken);
        Equal(new[] { $"{manifest.MigratorRole}|CONNECT", $"{manifest.RuntimeRole}|CONNECT" },
            databaseAcls.Select(row => string.Join('|', row)), "database ACLs");
        var schemaAcls = await RowsAsync(connection, """
            select n.nspname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_namespace n cross join lateral aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname=any(@schemas) and x.grantee<>n.nspowner order by 1,2,3
            """, cancellationToken, ("schemas", schemas));
        var expected = new[] { $"public|{manifest.MigratorRole}|USAGE", $"public|{manifest.RuntimeRole}|USAGE" }
            .Concat(manifest.ContextFunction is null ? [] : new[] { $"zeka|{manifest.RuntimeRole}|USAGE" });
        Equal(expected, schemaAcls.Select(row => string.Join('|', row)), "schema ACLs");
    }

    private static async Task VerifyTableAcls(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select c.relname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            cross join lateral aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname='public' and c.relkind in ('r','p')
              and coalesce(grantee.rolname,'PUBLIC')=any(@roles) order by c.relname,2,3
            """, cancellationToken, ("roles", new[] { manifest.RuntimeRole, manifest.MigratorRole, "PUBLIC" }));
        var expected = manifest.ProtectedTables.Concat(manifest.ExcludedTables)
            .SelectMany(table => Dml.Select(privilege => $"{table}|{manifest.RuntimeRole}|{privilege}"))
            .Concat(Dml.Select(privilege => $"__EFMigrationsHistory|{manifest.MigratorRole}|{privilege}"));
        Equal(expected, rows.Select(row => string.Join('|', row)), "table ACLs");
    }

    private static async Task VerifySequences(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select c.relname,owner.rolname from pg_class c join pg_namespace n on n.oid=c.relnamespace
            join pg_roles owner on owner.oid=c.relowner where n.nspname='public' and c.relkind='S' order by c.relname
            """, cancellationToken);
        Equal(manifest.Sequences, rows.Select(row => row[0]), "sequence inventory");
        if (rows.Any(row => row[1] != manifest.OwnerRole)) throw new InvalidOperationException("Sequence owner drifted.");
        var acls = await RowsAsync(connection, """
            select c.relname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            cross join lateral aclexplode(coalesce(c.relacl,acldefault('S',c.relowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname='public' and c.relkind='S'
              and coalesce(grantee.rolname,'PUBLIC')=any(@roles) order by c.relname,2,3
            """, cancellationToken, ("roles", new[] { manifest.RuntimeRole, "PUBLIC" }));
        var expected = manifest.Sequences.SelectMany(sequence => new[]
            { $"{sequence}|{manifest.RuntimeRole}|SELECT", $"{sequence}|{manifest.RuntimeRole}|USAGE" });
        Equal(expected, acls.Select(row => string.Join('|', row)), "sequence ACLs");
    }

    private static async Task VerifyFunctions(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var schemas = manifest.ContextFunction is null ? new[] { "public" } : new[] { "public", "zeka" };
        var rows = await RowsAsync(connection, """
            select n.nspname||'.'||p.proname||'('||pg_get_function_identity_arguments(p.oid)||')',owner.rolname,
              l.lanname,p.prosecdef::text,p.proleakproof::text,p.provolatile::text,coalesce(array_to_string(p.proconfig,','),'')
            from pg_proc p join pg_namespace n on n.oid=p.pronamespace join pg_roles owner on owner.oid=p.proowner
            join pg_language l on l.oid=p.prolang where n.nspname=any(@schemas) order by 1
            """, cancellationToken, ("schemas", schemas));
        Equal(manifest.RuntimeFunctions, rows.Select(row => row[0]), "function inventory");
        if (rows.Any(row => row[1] != manifest.OwnerRole || row[3] != "false" || row[4] != "false"))
            throw new InvalidOperationException("Function owner/security drifted.");
        if (manifest.ContextFunction is not null)
        {
            var context = rows.Single(row => row[0] == manifest.ContextFunction + "()");
            if (context[2] != "plpgsql" || context[5] != "v" || context[6] != "search_path=pg_catalog")
                throw new InvalidOperationException("Context function definition drifted.");
        }
        var acls = await RowsAsync(connection, """
            select n.nspname||'.'||p.proname||'('||pg_get_function_identity_arguments(p.oid)||')',
              coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_proc p join pg_namespace n on n.oid=p.pronamespace
            cross join lateral aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname=any(@schemas) and coalesce(grantee.rolname,'PUBLIC')=any(@roles) order by 1,2,3
            """, cancellationToken, ("schemas", schemas),
            ("roles", new[] { manifest.RuntimeRole, "PUBLIC" }));
        Equal(manifest.RuntimeFunctions.Select(function => $"{function}|{manifest.RuntimeRole}|EXECUTE"),
            acls.Select(row => string.Join('|', row)), "function ACLs");
    }

    private static async Task VerifyDefaultPrivileges(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select d.defaclobjtype::text,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type
            from pg_default_acl d join pg_roles owner on owner.oid=d.defaclrole
            left join pg_namespace n on n.oid=d.defaclnamespace cross join lateral aclexplode(d.defaclacl) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where owner.rolname=@owner and n.nspname='public'
              and coalesce(grantee.rolname,'PUBLIC')=any(@roles) order by 1,2,3
            """, cancellationToken, ("owner", manifest.OwnerRole),
            ("roles", new[] { manifest.RuntimeRole, "PUBLIC" }));
        var expected = Dml.Select(privilege => $"r|{manifest.RuntimeRole}|{privilege}")
            .Concat(new[] { $"S|{manifest.RuntimeRole}|SELECT", $"S|{manifest.RuntimeRole}|USAGE" });
        Equal(expected, rows.Select(row => string.Join('|', row)), "default privileges");
    }

    private static string Table(IEntityType type)
    {
        if (type.IsOwned()) throw new InvalidOperationException("Owned entity mapping is not allowed.");
        if (type.GetSchema() is not null and not "public") throw new InvalidOperationException("Unexpected mapped schema.");
        return type.GetTableName() ?? throw new InvalidOperationException("Entity is not mapped to a table.");
    }

    private static bool IsTenantOwned(Type type) => type.GetInterfaces().Any(contract =>
        contract.FullName == "Zeka.Extensions.MultiTenancy.Abstractions.ITenantOwnedEntity");

    private static void AssertRole(IReadOnlyCollection<string[]> rows, string role, bool login)
    {
        var row = rows.Single(value => value[0] == role);
        if (row[1] != login.ToString().ToLowerInvariant() || row.Skip(2).Take(6).Any(value => value != "false")
            || row[8] != "-1" || row[9] != string.Empty)
            throw new InvalidOperationException(
                $"Database role attributes drifted for {role}: [{string.Join(", ", row.Skip(1))}].");
    }

    private static string Normalize(string value) => string.Concat(value.Where(character =>
        !char.IsWhiteSpace(character) && character is not '(' and not ')')).Replace("\"", string.Empty,
        StringComparison.Ordinal);

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string category)
    {
        var left = expected.Order(StringComparer.Ordinal).ToArray();
        var right = actual.Order(StringComparer.Ordinal).ToArray();
        if (!left.SequenceEqual(right, StringComparer.Ordinal))
            throw new InvalidOperationException($"{category} drifted. Expected [{string.Join(", ", left)}], actual [{string.Join(", ", right)}].");
    }

    private static async Task<List<string[]>> RowsAsync(DbConnection connection, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++) row[index] = reader.GetValue(index).ToString() ?? string.Empty;
            rows.Add(row);
        }
        return rows;
    }
}
