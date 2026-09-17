using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Zeka.PersistenceSecurity;

public sealed record DefaultPrivilegeGrant(string Grantee, string Privilege, bool Grantable);

public sealed record DefaultPrivilegeState(string Owner, string ObjectType, string Scope, string? Schema,
    DefaultPrivilegeGrant[] Grants);

public sealed record ParameterPrivilegeGrant(string Parameter, string Grantee, string Privilege, bool Grantable);

public sealed record ManagedObjectIdentity(string Schema, string Name);

public sealed record RelationTopologyState(string Kind, ManagedObjectIdentity Parent,
    ManagedObjectIdentity Child, int Sequence, bool DetachPending);

public sealed record RlsSecurityManifest(int SchemaVersion, string Service, string ModelContext,
    string MigrationId, string OwnerRole, string MigratorRole, string RuntimeRole,
    string[] ManagedSchemas, string[] ProhibitedRelationKinds,
    RelationTopologyState[] RelationTopology,
    ManagedObjectIdentity[] ProtectedTables, ManagedObjectIdentity[] ExcludedTables,
    ManagedObjectIdentity MigrationHistoryTable, ManagedObjectIdentity[] Sequences,
    string[] RuntimeFunctions, Dictionary<string, string> RuntimeFunctionDefinitionSha256,
    DefaultPrivilegeState[] DefaultPrivileges, ParameterPrivilegeGrant[] ParameterPrivileges,
    string PolicyPrefix, string? ContextFunction);

/// <summary>Deployment-only bidirectional comparison of the versioned model and effective PostgreSQL state.</summary>
public static class RlsSecurityManifestVerifier
{
    private static readonly string[] Dml = ["DELETE", "INSERT", "SELECT", "UPDATE"];
    private static readonly string[] ProhibitedRelationKinds =
    [
        "FOREIGN_TABLE",
        "MATERIALIZED_VIEW",
        "VIEW"
    ];

    public static RlsSecurityManifest Load(Assembly assembly)
    {
        var name = assembly.GetManifestResourceNames().Single(x =>
            x.EndsWith("rls-manifest.v8.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("RLS manifest resource is missing.");
        return JsonSerializer.Deserialize<RlsSecurityManifest>(stream, new JsonSerializerOptions
        { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("RLS manifest is malformed.");
    }

    public static IReadOnlyCollection<ManagedObjectIdentity> GenerateProtectedInventory(DbContext database) => database.Model
        .GetEntityTypes().Where(type => IsTenantOwned(type.ClrType))
        .Select(Table).Distinct().OrderBy(identity => identity.Schema, StringComparer.Ordinal)
        .ThenBy(identity => identity.Name, StringComparer.Ordinal).ToArray();

    public static async Task VerifyAsync(DbContext database, Assembly manifestAssembly,
        CancellationToken cancellationToken = default)
    {
        var manifest = Load(manifestAssembly);
        if (manifest.SchemaVersion != 8 || database.GetType().FullName != manifest.ModelContext)
            throw new InvalidOperationException("Manifest identity does not match the deployment model.");
        Equal(manifest.RuntimeFunctions, manifest.RuntimeFunctionDefinitionSha256.Keys,
            "function definition inventory");
        ValidateDefaultPrivilegeManifest(manifest);
        ValidateParameterPrivilegeManifest(manifest);
        ValidateManagedObjectManifest(manifest);
        if (manifest.ProtectedTables.Intersect(manifest.ExcludedTables).Any()
            || manifest.ProtectedTables.Contains(manifest.MigrationHistoryTable)
            || manifest.ExcludedTables.Contains(manifest.MigrationHistoryTable))
            throw new InvalidOperationException("Protected and excluded inventories overlap.");
        var mapped = database.Model.GetEntityTypes().Select(Table).Distinct().ToArray();
        if (mapped.Any(identity => !manifest.ManagedSchemas.Contains(identity.Schema, StringComparer.Ordinal)))
            throw new InvalidOperationException("An EF mapping targets an undeclared managed schema.");
        Equal(manifest.ProtectedTables.Select(ObjectKey), GenerateProtectedInventory(database).Select(ObjectKey),
            "EF protected inventory");
        Equal(manifest.ProtectedTables.Concat(manifest.ExcludedTables).Select(ObjectKey), mapped.Select(ObjectKey),
            "EF complete classification");
        foreach (var type in database.Model.GetEntityTypes().Where(type =>
                     manifest.ProtectedTables.Contains(Table(type))))
            if (type.FindProperty("OrganisationId")?.ClrType != typeof(Guid)
                || !IsTenantOwned(type.ClrType))
                throw new InvalidOperationException("A protected mapping lacks tenant ownership or UUID OrganisationId.");

        var connection = database.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        if (close) await connection.OpenAsync(cancellationToken);
        try
        {
            await VerifyRoles(connection, manifest, cancellationToken);
            await VerifyParameterPrivileges(connection, manifest, cancellationToken);
            await VerifyRelationTopology(connection, manifest, cancellationToken);
            await VerifyProhibitedRelationSurfaces(connection, manifest, cancellationToken);
            await VerifyTablesAndPolicies(connection, manifest, cancellationToken);
            await VerifyDatabaseAndSchemas(connection, manifest, cancellationToken);
            await VerifyTableAcls(connection, manifest, cancellationToken);
            await VerifyColumnAcls(connection, manifest, cancellationToken);
            await VerifySequences(connection, manifest, cancellationToken);
            await VerifyFunctions(connection, manifest, cancellationToken);
            await VerifyDefaultPrivileges(connection, manifest, cancellationToken);
        }
        finally { if (close) await connection.CloseAsync(); }
    }

    private static async Task VerifyRelationTopology(DbConnection connection,
        RlsSecurityManifest manifest, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select case when child.relispartition then 'PARTITION' else 'INHERITANCE' end,
              parent_schema.nspname,parent.relname,child_schema.nspname,child.relname,
              inheritance.inhseqno::text,inheritance.inhdetachpending::text
            from pg_inherits inheritance
            join pg_class child on child.oid=inheritance.inhrelid
            join pg_namespace child_schema on child_schema.oid=child.relnamespace
            join pg_class parent on parent.oid=inheritance.inhparent
            join pg_namespace parent_schema on parent_schema.oid=parent.relnamespace
            where (child_schema.nspname=any(@schemas) or parent_schema.nspname=any(@schemas))
              and child.relkind in ('r','p','f') and parent.relkind in ('r','p','f')
            order by 1,2,3,4,5,6,7
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        var expected = manifest.RelationTopology.Select(state => string.Join('|',
            state.Kind, state.Parent.Schema, state.Parent.Name, state.Child.Schema, state.Child.Name,
            state.Sequence, state.DetachPending.ToString().ToLowerInvariant()));
        Equal(expected, rows.Select(row => string.Join('|', row)), "relation topology");
    }

    private static async Task VerifyProhibitedRelationSurfaces(DbConnection connection,
        RlsSecurityManifest manifest, CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select n.nspname,c.relname,
              case c.relkind
                when 'f' then 'FOREIGN_TABLE'
                when 'm' then 'MATERIALIZED_VIEW'
                when 'v' then 'VIEW'
              end,
              owner.rolname
            from pg_class c
            join pg_namespace n on n.oid=c.relnamespace
            join pg_roles owner on owner.oid=c.relowner
            where n.nspname=any(@schemas) and c.relkind in ('f','m','v')
            order by n.nspname,c.relname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal([], rows.Select(row => string.Join('|', row)), "prohibited relation surfaces");
    }

    private static async Task VerifyParameterPrivileges(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var managed = new[] { manifest.OwnerRole, manifest.MigratorRole, manifest.RuntimeRole };
        var rows = await RowsAsync(connection, """
            select lower(parameter.parname),coalesce(grantee.rolname,'PUBLIC'),
              acl.privilege_type,acl.is_grantable::text
            from pg_parameter_acl parameter
            cross join lateral aclexplode(parameter.paracl) acl
            left join pg_roles grantee on grantee.oid=acl.grantee
            where acl.grantee=0 or grantee.rolname=any(@roles)
            order by 1,2,3,4
            """, cancellationToken, ("roles", managed));
        var expected = manifest.ParameterPrivileges.Select(grant =>
            $"{grant.Parameter.ToLowerInvariant()}|{grant.Grantee}|{grant.Privilege}|{grant.Grantable.ToString().ToLowerInvariant()}");
        Equal(expected, rows.Select(row => string.Join('|', row)), "parameter privileges");
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
            select n.nspname,c.relname,owner.rolname,c.relrowsecurity::text,c.relforcerowsecurity::text
            from pg_class c join pg_namespace n on n.oid=c.relnamespace join pg_roles owner on owner.oid=c.relowner
            where n.nspname=any(@schemas) and c.relkind in ('r','p') order by n.nspname,c.relname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        var expectedTables = manifest.ProtectedTables.Concat(manifest.ExcludedTables)
            .Append(manifest.MigrationHistoryTable).Select(ObjectKey);
        Equal(expectedTables, tables.Select(row => ObjectKey(row[0], row[1])), "catalog tables");
        foreach (var row in tables)
        {
            var identity = new ManagedObjectIdentity(row[0], row[1]);
            var tenant = manifest.ProtectedTables.Contains(identity);
            if (row[2] != manifest.OwnerRole || row[3] != tenant.ToString().ToLowerInvariant()
                || row[4] != tenant.ToString().ToLowerInvariant())
                throw new InvalidOperationException("Table ownership or RLS state drifted.");
        }

        var organisationColumns = await RowsAsync(connection, """
            select n.nspname,c.relname,format_type(a.atttypid,a.atttypmod),a.attnotnull::text
            from pg_attribute a join pg_class c on c.oid=a.attrelid
            join pg_namespace n on n.oid=c.relnamespace
            where n.nspname=any(@schemas) and c.relkind in ('r','p')
              and a.attnum>0 and not a.attisdropped and a.attname='OrganisationId'
            order by n.nspname,c.relname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        var classified = manifest.ProtectedTables.Concat(manifest.ExcludedTables).ToHashSet();
        if (organisationColumns.Any(row => !classified.Contains(new ManagedObjectIdentity(row[0], row[1]))))
            throw new InvalidOperationException("An OrganisationId table lacks a reviewed classification.");
        foreach (var table in manifest.ProtectedTables)
        {
            var column = organisationColumns.SingleOrDefault(row => row[0] == table.Schema && row[1] == table.Name);
            if (column is null || column[2] != "uuid" || column[3] != "true")
                throw new InvalidOperationException("A protected table lacks non-null UUID OrganisationId.");
        }

        var historyIdentity = manifest.MigrationHistoryTable;
        var history = await RowsAsync(connection, $"""
            select owner.rolname,c.relrowsecurity::text,c.relforcerowsecurity::text,
              (select h."MigrationId" from {QualifiedSql(historyIdentity)} h order by h."MigrationId" desc limit 1)
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            join pg_roles owner on owner.oid=c.relowner
            where n.nspname=@schema and c.relname=@table and c.relkind='r'
            """, cancellationToken, ("schema", historyIdentity.Schema), ("table", historyIdentity.Name));
        if (history.Count != 1 || history[0][0] != manifest.OwnerRole
            || history[0][1] != "false" || history[0][2] != "false"
            || history[0][3] != manifest.MigrationId)
            throw new InvalidOperationException("Migration-history identity or ownership drifted.");
        var policies = await RowsAsync(connection, """
            select n.nspname,c.relname,p.polname,p.polcmd::text,p.polpermissive::text,
                   pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid),
                   array_to_string(array(select rolname from pg_roles where oid=any(p.polroles) order by rolname),',')
            from pg_policy p join pg_class c on c.oid=p.polrelid join pg_namespace n on n.oid=c.relnamespace
            where n.nspname=any(@schemas) order by n.nspname,c.relname,p.polname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal(manifest.ProtectedTables.Select(ObjectKey),
            policies.Select(row => ObjectKey(row[0], row[1])), "RLS policy targets");
        var expression = Normalize("\"OrganisationId\" = zeka.current_organisation_id()");
        foreach (var row in policies)
            if (row[2] != manifest.PolicyPrefix + row[1].ToLowerInvariant() + "_organisation"
                || row[3] != "*" || row[4] != "true" || row[7] != manifest.RuntimeRole
                || Normalize(row[5]) != expression || Normalize(row[6]) != expression)
                throw new InvalidOperationException("RLS policy definition drifted.");
    }

    private static async Task VerifyDatabaseAndSchemas(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var owners = await RowsAsync(connection, """
            select n.nspname,owner.rolname from pg_namespace n join pg_roles owner on owner.oid=n.nspowner
            where n.nspname=any(@schemas) order by n.nspname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal(manifest.ManagedSchemas, owners.Select(row => row[0]), "schema inventory");
        if (owners.Any(row => row[1] != manifest.OwnerRole)) throw new InvalidOperationException("Schema owner drifted.");
        var ownerSchemas = await RowsAsync(connection, """
            select n.nspname from pg_namespace n join pg_roles owner on owner.oid=n.nspowner
            where owner.rolname=@owner and n.nspname!~'^pg_' and n.nspname<>'information_schema'
            order by n.nspname
            """, cancellationToken, ("owner", manifest.OwnerRole));
        Equal(manifest.ManagedSchemas, ownerSchemas.Select(row => row[0]), "managed owner schema inventory");
        var databaseAcls = await RowsAsync(connection, """
            select coalesce(grantee.rolname,'PUBLIC'),x.privilege_type,x.is_grantable::text
            from pg_database d cross join lateral aclexplode(coalesce(d.datacl,acldefault('d',d.datdba))) x
            left join pg_roles grantee on grantee.oid=x.grantee left join pg_roles owner on owner.oid=d.datdba
            -- Owners have inherent privileges and their identity is verified separately.
            where d.datname=current_database() and x.grantee<>d.datdba order by 1,2
            """, cancellationToken);
        Equal(new[] { $"{manifest.MigratorRole}|CONNECT|false", $"{manifest.RuntimeRole}|CONNECT|false" },
            databaseAcls.Select(row => string.Join('|', row)), "database ACLs");
        var schemaAcls = await RowsAsync(connection, """
            select n.nspname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type,x.is_grantable::text
            from pg_namespace n cross join lateral aclexplode(coalesce(n.nspacl,acldefault('n',n.nspowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            -- Owners have inherent privileges and their identity is verified separately.
            where n.nspname=any(@schemas) and x.grantee<>n.nspowner order by 1,2,3
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        // The ordered inventory starts with the service persistence/migration schema.
        var migrationSchema = manifest.ManagedSchemas[0];
        var expected = manifest.ManagedSchemas
            .Select(schema => $"{schema}|{manifest.RuntimeRole}|USAGE|false")
            .Append($"{migrationSchema}|{manifest.MigratorRole}|USAGE|false");
        Equal(expected, schemaAcls.Select(row => string.Join('|', row)), "schema ACLs");
    }

    private static async Task VerifyTableAcls(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select n.nspname,c.relname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type,x.is_grantable::text
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            cross join lateral aclexplode(coalesce(c.relacl,acldefault('r',c.relowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname=any(@schemas) and c.relkind in ('r','p')
              and x.grantee<>c.relowner order by n.nspname,c.relname,3,4,5
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        var expected = manifest.ProtectedTables.Concat(manifest.ExcludedTables)
            .SelectMany(table => Dml.Select(privilege =>
                $"{ObjectKey(table)}|{manifest.RuntimeRole}|{privilege}|false"))
            .Concat(Dml.Select(privilege =>
                $"{ObjectKey(manifest.MigrationHistoryTable)}|{manifest.MigratorRole}|{privilege}|false"));
        Equal(expected, rows.Select(row => string.Join('|', row)), "table ACLs");
    }

    private static async Task VerifyColumnAcls(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select n.nspname,c.relname,a.attname,coalesce(grantee.rolname,'PUBLIC'),
              x.privilege_type,x.is_grantable::text
            from pg_attribute a join pg_class c on c.oid=a.attrelid
            join pg_namespace n on n.oid=c.relnamespace
            cross join lateral aclexplode(a.attacl) x
            left join pg_roles grantee on grantee.oid=x.grantee
            -- Owners have inherent privileges and their identity is verified separately.
            where n.nspname=any(@schemas) and c.relkind in ('r','p')
              and a.attnum>0 and not a.attisdropped and a.attacl is not null
              and x.grantee<>c.relowner order by 1,2,3,4,5,6
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal([], rows.Select(row => string.Join('|', row)), "column ACLs");
    }

    private static async Task VerifySequences(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select n.nspname,c.relname,owner.rolname from pg_class c join pg_namespace n on n.oid=c.relnamespace
            join pg_roles owner on owner.oid=c.relowner
            where n.nspname=any(@schemas) and c.relkind='S' order by n.nspname,c.relname
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal(manifest.Sequences.Select(ObjectKey), rows.Select(row => ObjectKey(row[0], row[1])),
            "sequence inventory");
        if (rows.Any(row => row[2] != manifest.OwnerRole)) throw new InvalidOperationException("Sequence owner drifted.");
        var acls = await RowsAsync(connection, """
            select n.nspname,c.relname,coalesce(grantee.rolname,'PUBLIC'),x.privilege_type,x.is_grantable::text
            from pg_class c join pg_namespace n on n.oid=c.relnamespace
            cross join lateral aclexplode(coalesce(c.relacl,acldefault('S',c.relowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname=any(@schemas) and c.relkind='S'
              and x.grantee<>c.relowner order by n.nspname,c.relname,3,4,5
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        var expected = manifest.Sequences.SelectMany(sequence => new[]
            { $"{ObjectKey(sequence)}|{manifest.RuntimeRole}|SELECT|false",
                $"{ObjectKey(sequence)}|{manifest.RuntimeRole}|USAGE|false" });
        Equal(expected, acls.Select(row => string.Join('|', row)), "sequence ACLs");
    }

    private static async Task VerifyFunctions(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var rows = await RowsAsync(connection, """
            select n.nspname||'.'||p.proname||'('||pg_get_function_identity_arguments(p.oid)||')',owner.rolname,
              l.lanname,p.prosecdef::text,p.proleakproof::text,p.provolatile::text,
              coalesce(array_to_string(p.proconfig,','),''),pg_get_functiondef(p.oid)
            from pg_proc p join pg_namespace n on n.oid=p.pronamespace join pg_roles owner on owner.oid=p.proowner
            join pg_language l on l.oid=p.prolang where n.nspname=any(@schemas) order by 1
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal(manifest.RuntimeFunctions, rows.Select(row => row[0]), "function inventory");
        if (rows.Any(row => row[1] != manifest.OwnerRole || row[3] != "false" || row[4] != "false"))
            throw new InvalidOperationException("Function owner/security drifted.");
        if (manifest.ContextFunction is not null)
        {
            var context = rows.Single(row => row[0] == manifest.ContextFunction + "()");
            if (context[2] != "plpgsql" || context[5] != "v" || context[6] != "search_path=pg_catalog")
                throw new InvalidOperationException("Context function definition drifted.");
        }
        foreach (var row in rows)
        {
            var actual = DefinitionSha256(row[7]);
            var expectedDefinition = manifest.RuntimeFunctionDefinitionSha256[row[0]];
            if (!StringComparer.OrdinalIgnoreCase.Equals(expectedDefinition, actual))
                throw new InvalidOperationException(
                    $"Function body drifted for {row[0]}. Expected {expectedDefinition}, actual {actual}.");
        }
        var acls = await RowsAsync(connection, """
            select n.nspname||'.'||p.proname||'('||pg_get_function_identity_arguments(p.oid)||')',
              coalesce(grantee.rolname,'PUBLIC'),x.privilege_type,x.is_grantable::text
            from pg_proc p join pg_namespace n on n.oid=p.pronamespace
            cross join lateral aclexplode(coalesce(p.proacl,acldefault('f',p.proowner))) x
            left join pg_roles grantee on grantee.oid=x.grantee
            where n.nspname=any(@schemas) and x.grantee<>p.proowner order by 1,2,3,4
            """, cancellationToken, ("schemas", manifest.ManagedSchemas));
        Equal(manifest.RuntimeFunctions.Select(function => $"{function}|{manifest.RuntimeRole}|EXECUTE|false"),
            acls.Select(row => string.Join('|', row)), "function ACLs");
    }

    private static async Task VerifyDefaultPrivileges(DbConnection connection, RlsSecurityManifest manifest,
        CancellationToken cancellationToken)
    {
        var schemaScopes = await RowsAsync(connection, """
            select distinct n.nspname from pg_default_acl d
            join pg_roles owner on owner.oid=d.defaclrole
            join pg_namespace n on n.oid=d.defaclnamespace
            where owner.rolname=@owner and d.defaclnamespace<>0
            order by n.nspname
            """, cancellationToken, ("owner", manifest.OwnerRole));
        var undeclaredScopes = schemaScopes.Select(row => row[0])
            .Except(manifest.ManagedSchemas, StringComparer.Ordinal).ToArray();
        if (undeclaredScopes.Length != 0)
            throw new InvalidOperationException(
                $"Default privileges target undeclared schemas: [{string.Join(", ", undeclaredScopes)}].");

        var rows = await RowsAsync(connection, """
            with global_types(code,name) as (
              values ('r'::"char",'RELATION'),('S'::"char",'SEQUENCE'),('f'::"char",'FUNCTION'),
                ('T'::"char",'TYPE'),('n'::"char",'SCHEMA')
            ), schema_types(code,name) as (
              values ('r'::"char",'RELATION'),('S'::"char",'SEQUENCE'),('f'::"char",'FUNCTION'),
                ('T'::"char",'TYPE')
            ), managed_owner as (
              select oid,rolname from pg_roles where rolname=@owner
            ), effective_global as (
              select owner.rolname,'GLOBAL' scope,type.name object_type,
                coalesce(grantee.rolname,'PUBLIC') grantee,x.privilege_type,x.is_grantable::text grantable
              from managed_owner owner cross join global_types type
              left join pg_default_acl d on d.defaclrole=owner.oid and d.defaclnamespace=0
                and d.defaclobjtype=type.code
              cross join lateral aclexplode(coalesce(d.defaclacl,acldefault(type.code,owner.oid))) x
              left join pg_roles grantee on grantee.oid=x.grantee
              -- Owners have inherent privileges and their identity is verified separately.
              where x.grantee<>owner.oid
            ), schema_additions as (
              select owner.rolname,'SCHEMA '||n.nspname scope,type.name object_type,
                coalesce(grantee.rolname,'PUBLIC') grantee,x.privilege_type,x.is_grantable::text grantable
              from pg_default_acl d join managed_owner owner on owner.oid=d.defaclrole
              join pg_namespace n on n.oid=d.defaclnamespace
              join schema_types type on type.code=d.defaclobjtype
              cross join lateral aclexplode(d.defaclacl) x
              left join pg_roles grantee on grantee.oid=x.grantee
              -- Schema ACLs are additions to global defaults; compare them independently.
              where x.grantee<>owner.oid
            )
            select * from effective_global union all select * from schema_additions
            order by 1,2,3,4,5,6
            """, cancellationToken, ("owner", manifest.OwnerRole));
        var expected = manifest.DefaultPrivileges.SelectMany(state => state.Grants.Select(grant =>
            $"{state.Owner}|{DefaultPrivilegeScope(state)}|{state.ObjectType}|{grant.Grantee}|{grant.Privilege}|{grant.Grantable.ToString().ToLowerInvariant()}"));
        Equal(expected, rows.Select(row => string.Join('|', row)), "default privileges");
    }

    private static void ValidateDefaultPrivilegeManifest(RlsSecurityManifest manifest)
    {
        if (manifest.ManagedSchemas.Length == 0
            || !manifest.ManagedSchemas.SequenceEqual(
                manifest.ManagedSchemas.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                StringComparer.Ordinal)
            || manifest.ManagedSchemas.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("Managed schema inventory is invalid.");
        var expectedScopes = new[] { "RELATION", "SEQUENCE", "FUNCTION", "TYPE", "SCHEMA" }
            .Select(type => $"{manifest.OwnerRole}|GLOBAL|{type}")
            .Concat(manifest.ManagedSchemas.SelectMany(schema =>
                new[] { "RELATION", "SEQUENCE", "FUNCTION", "TYPE" }.Select(type =>
                    $"{manifest.OwnerRole}|SCHEMA {schema}|{type}")));
        Equal(expectedScopes, manifest.DefaultPrivileges.Select(state =>
            $"{state.Owner}|{DefaultPrivilegeScope(state)}|{state.ObjectType}"),
            "default privilege scope inventory");
        foreach (var state in manifest.DefaultPrivileges)
        {
            if (state.Grants.Any(grant => grant.Grantee == manifest.OwnerRole))
                throw new InvalidOperationException("Default privilege owner grants must use the reviewed owner exception.");
            Equal(state.Grants.Select(grant =>
                    $"{grant.Grantee}|{grant.Privilege}|{grant.Grantable}"),
                state.Grants.Distinct().Select(grant =>
                    $"{grant.Grantee}|{grant.Privilege}|{grant.Grantable}"),
                "default privilege grants");
        }
    }

    private static void ValidateParameterPrivilegeManifest(RlsSecurityManifest manifest)
    {
        var managed = new[] { "PUBLIC", manifest.OwnerRole, manifest.MigratorRole, manifest.RuntimeRole };
        foreach (var grant in manifest.ParameterPrivileges)
        {
            if (string.IsNullOrWhiteSpace(grant.Parameter)
                || !managed.Contains(grant.Grantee, StringComparer.Ordinal)
                || grant.Privilege is not ("SET" or "ALTER SYSTEM"))
                throw new InvalidOperationException("Parameter privilege manifest entry is invalid.");
        }
        Equal(manifest.ParameterPrivileges.Select(grant =>
                $"{grant.Parameter.ToLowerInvariant()}|{grant.Grantee}|{grant.Privilege}|{grant.Grantable}"),
            manifest.ParameterPrivileges.DistinctBy(grant =>
                $"{grant.Parameter.ToLowerInvariant()}|{grant.Grantee}|{grant.Privilege}|{grant.Grantable}")
                .Select(grant =>
                    $"{grant.Parameter.ToLowerInvariant()}|{grant.Grantee}|{grant.Privilege}|{grant.Grantable}"),
            "parameter privilege grants");
    }

    private static void ValidateManagedObjectManifest(RlsSecurityManifest manifest)
    {
        Equal(ProhibitedRelationKinds, manifest.ProhibitedRelationKinds,
            "prohibited relation kind inventory");
        if (manifest.RelationTopology.Length != 0)
            throw new InvalidOperationException(
                "The current security model prohibits inheritance and partition topology.");
        ValidateIdentities(manifest.ProtectedTables, "protected table");
        ValidateIdentities(manifest.ExcludedTables, "excluded table");
        ValidateIdentities(manifest.Sequences, "sequence");
        var history = manifest.MigrationHistoryTable;
        if (string.IsNullOrWhiteSpace(history.Schema) || string.IsNullOrWhiteSpace(history.Name)
            || !manifest.ManagedSchemas.Contains(history.Schema, StringComparer.Ordinal))
            throw new InvalidOperationException("Migration-history table identity is invalid.");

        void ValidateIdentities(IEnumerable<ManagedObjectIdentity> identities, string category)
        {
            var keys = identities.Select(identity =>
            {
                if (string.IsNullOrWhiteSpace(identity.Schema) || string.IsNullOrWhiteSpace(identity.Name)
                    || !manifest.ManagedSchemas.Contains(identity.Schema, StringComparer.Ordinal))
                    throw new InvalidOperationException($"A {category} identity is invalid.");
                return ObjectKey(identity);
            }).ToArray();
            if (!keys.SequenceEqual(keys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw new InvalidOperationException($"The {category} inventory is invalid.");
        }
    }

    private static string DefaultPrivilegeScope(DefaultPrivilegeState state) => state.Scope switch
    {
        "GLOBAL" when state.Schema is null => "GLOBAL",
        "SCHEMA" when !string.IsNullOrWhiteSpace(state.Schema) => $"SCHEMA {state.Schema}",
        _ => throw new InvalidOperationException("Default privilege scope is invalid.")
    };

    private static ManagedObjectIdentity Table(IEntityType type)
    {
        if (type.IsOwned()) throw new InvalidOperationException("Owned entity mapping is not allowed.");
        return new ManagedObjectIdentity(type.GetSchema() ?? "public",
            type.GetTableName() ?? throw new InvalidOperationException("Entity is not mapped to a table."));
    }

    private static string ObjectKey(ManagedObjectIdentity identity) => ObjectKey(identity.Schema, identity.Name);

    private static string ObjectKey(string schema, string name) => $"{schema}|{name}";

    private static string QualifiedSql(ManagedObjectIdentity identity) =>
        $"\"{identity.Schema.Replace("\"", "\"\"", StringComparison.Ordinal)}\".\"{identity.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

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

    private static string DefinitionSha256(string definition)
    {
        // pg_get_functiondef appends a presentation newline. Preserve all body whitespace: SQL function
        // literals can contain NEL and Unicode line separators whose normalization would change semantics.
        var canonical = definition.TrimEnd('\r', '\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

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
