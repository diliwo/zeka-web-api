using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using NpgsqlTypes;
using Zeka.OwnerSqlEvidence;

namespace Zeka.DbMigrate;

internal static class AuthOnboardingSchemaVerifier
{
    private const string Target = "20261115195218_OnboardingRegistration";
    private static readonly string[] Tables =
    ["EmailVerificationChallenges", "OnboardingGrants", "OnboardingOperationReceipts", "VerificationMailOutboxMessages"];

    internal static async Task VerifyAsync(DbContext context, NpgsqlConnection connection,
        NpgsqlTransaction transaction, CancellationToken token)
    {
        Require(ReferenceEquals(context.Database.GetDbConnection(), connection)
            && ReferenceEquals(transaction.Connection, connection));
        var expected = ReadExpected(context);
        await using var command = new NpgsqlCommand("""
            WITH target_relations AS (
              SELECT c.* FROM pg_catalog.pg_class c
              JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
              WHERE n.nspname::text='public' AND c.relname::text=ANY(@target_tables)
            )
            SELECT pg_catalog.jsonb_build_object(
              'tables', COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                'name',t.relname::text,'kind',t.relkind::text,'persistence',t.relpersistence::text,
                'partition',t.relispartition,'typed',t.reloftype<>0,
                'rls',t.relrowsecurity,'forceRls',t.relforcerowsecurity,
                'inherits',EXISTS(SELECT 1 FROM pg_catalog.pg_inherits h
                  WHERE h.inhrelid=t.oid OR h.inhparent=t.oid),
                'columns',COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                  'name',a.attname::text,'type',pg_catalog.format_type(a.atttypid,a.atttypmod),
                  'nullable',NOT a.attnotnull,'dropped',a.attisdropped,
                  'identity',a.attidentity::text,'generated',a.attgenerated::text,
                  'defaultExpression',pg_catalog.pg_get_expr(d.adbin,d.adrelid),
                  'defaultCollation',a.attcollation=ty.typcollation)
                  ORDER BY a.attnum)
                  FROM pg_catalog.pg_attribute a
                  LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
                  LEFT JOIN pg_catalog.pg_type ty ON ty.oid=a.atttypid
                  WHERE a.attrelid=t.oid AND a.attnum>0),'[]'::jsonb)) ORDER BY t.relname)
                FROM target_relations t),'[]'::jsonb),
              'constraints', COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                'table',t.relname::text,'name',c.conname::text,'kind',c.contype::text,
                'deferrable',c.condeferrable,'deferred',c.condeferred,'validated',c.convalidated,
                'local',c.conislocal,'inheritCount',c.coninhcount,
                'columns',COALESCE((SELECT pg_catalog.jsonb_agg(a.attname::text ORDER BY k.ordinality)
                  FROM pg_catalog.unnest(c.conkey) WITH ORDINALITY k(attnum,ordinality)
                  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum),'[]'::jsonb),
                'principalSchema',rn.nspname::text,'principalTable',r.relname::text,
                'principalColumns',COALESCE((SELECT pg_catalog.jsonb_agg(a.attname::text ORDER BY k.ordinality)
                  FROM pg_catalog.unnest(c.confkey) WITH ORDINALITY k(attnum,ordinality)
                  JOIN pg_catalog.pg_attribute a ON a.attrelid=c.confrelid AND a.attnum=k.attnum),'[]'::jsonb),
                'updateAction',c.confupdtype::text,'deleteAction',c.confdeltype::text)
                ORDER BY t.relname,c.conname)
                FROM target_relations t JOIN pg_catalog.pg_constraint c ON c.conrelid=t.oid
                LEFT JOIN pg_catalog.pg_class r ON r.oid=c.confrelid
                LEFT JOIN pg_catalog.pg_namespace rn ON rn.oid=r.relnamespace),'[]'::jsonb),
              'indexes', COALESCE((SELECT pg_catalog.jsonb_agg(pg_catalog.jsonb_build_object(
                'table',t.relname::text,'name',ci.relname::text,'method',am.amname::text,
                'unique',i.indisunique,'primary',i.indisprimary,'valid',i.indisvalid,
                'ready',i.indisready,'live',i.indislive,'immediate',i.indimmediate,
                'nullsNotDistinct',i.indnullsnotdistinct,'keyCount',i.indnkeyatts,'attributeCount',i.indnatts,
                'expressions',i.indexprs IS NOT NULL,'predicate',pg_catalog.pg_get_expr(i.indpred,i.indrelid),
                'columns',COALESCE((SELECT pg_catalog.jsonb_agg(a.attname::text ORDER BY k.ordinality)
                  FROM pg_catalog.unnest(i.indkey::smallint[]) WITH ORDINALITY k(attnum,ordinality)
                  LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum),'[]'::jsonb),
                'defaultOrdering',NOT EXISTS(SELECT 1 FROM pg_catalog.unnest(i.indoption::smallint[]) v(value)
                  WHERE v.value<>0),
                'defaultOpclasses',NOT EXISTS(SELECT 1 FROM pg_catalog.unnest(i.indclass::oid[]) v(value)
                  JOIN pg_catalog.pg_opclass op ON op.oid=v.value WHERE NOT op.opcdefault),
                'defaultCollation',NOT EXISTS(
                  SELECT 1 FROM pg_catalog.unnest(i.indcollation::oid[]) WITH ORDINALITY co(value,ordinality)
                  JOIN pg_catalog.unnest(i.indkey::smallint[]) WITH ORDINALITY k(attnum,ordinality)
                    ON k.ordinality=co.ordinality
                  LEFT JOIN pg_catalog.pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum
                  WHERE co.value IS DISTINCT FROM a.attcollation),
                'primaryConstraint',(SELECT p.conname::text FROM pg_catalog.pg_constraint p
                  WHERE p.conrelid=i.indrelid AND p.conindid=i.indexrelid AND p.contype='p'))
                ORDER BY t.relname,ci.relname)
                FROM target_relations t JOIN pg_catalog.pg_index i ON i.indrelid=t.oid
                JOIN pg_catalog.pg_class ci ON ci.oid=i.indexrelid
                JOIN pg_catalog.pg_am am ON am.oid=ci.relam),'[]'::jsonb))::text
            """, connection, transaction) { CommandTimeout = 30 };
        command.Parameters.AddWithValue("target_tables", NpgsqlDbType.Array | NpgsqlDbType.Text, Tables);
        var payload = await OwnerSqlExecution.ExecuteScalarAsync(command, token);
        Require(payload is string && ((string)payload).Length <= 262144);
        JsonDocument document;
        try { document = JsonDocument.Parse((string)payload!); }
        catch (JsonException) { throw Rejected(); }
        using (document) VerifySnapshot(document.RootElement, expected);
    }

    private static ExpectedSchema ReadExpected(DbContext context)
    {
        Require(context.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL");
        var assembly = context.GetService<IMigrationsAssembly>();
        Require(assembly.Migrations.TryGetValue(Target, out var type));
        var migration = assembly.CreateMigration(type!, context.Database.ProviderName!);
        var operations = migration.UpOperations;
        var tables = operations.OfType<CreateTableOperation>().OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
        var indexes = operations.OfType<CreateIndexOperation>().ToArray();
        Require(operations.Count == 13 && tables.Length == 4 && indexes.Length == 9
            && tables.Select(x => x.Name).SequenceEqual(Tables, StringComparer.Ordinal)
            && tables.Sum(x => x.Columns.Count) == 43);
        foreach (var table in tables)
        {
            Require(IsPublic(table.Schema) && table.PrimaryKey is not null && table.ForeignKeys.Count == 1
                && table.UniqueConstraints.Count == 0 && table.CheckConstraints.Count == 0);
            foreach (var column in table.Columns)
                Require(!string.IsNullOrEmpty(column.ColumnType) && column.DefaultValue is null
                    && column.DefaultValueSql is null && column.ComputedColumnSql is null);
            Require(table.ForeignKeys.All(x => IsPublic(x.PrincipalSchema)
                && x.OnDelete == ReferentialAction.Restrict && x.OnUpdate == ReferentialAction.NoAction
                && x.PrincipalColumns is not null));
        }
        foreach (var index in indexes)
            Require(IsPublic(index.Schema) && Tables.Contains(index.Table, StringComparer.Ordinal)
                && index.Columns.Length > 0
                && (index.IsDescending is null || (index.IsDescending.Length == index.Columns.Length
                    && index.IsDescending.All(value => !value)))
                && (index.Filter is null || (index.Table == "EmailVerificationChallenges"
                    && index.Name == "IX_EmailVerificationChallenges_UserId"
                    && index.Filter == "\"Status\" = 'Pending'")));
        return new ExpectedSchema(tables, indexes);
    }

    private static void VerifySnapshot(JsonElement root, ExpectedSchema expected)
    {
        Fields(root, "tables", "constraints", "indexes");
        var tables = Unique(Rows(root, "tables"), row => Text(row, "name"));
        Require(tables.Count == 4 && tables.Keys.Order(StringComparer.Ordinal).SequenceEqual(Tables, StringComparer.Ordinal));
        foreach (var table in expected.Tables)
        {
            Require(tables.TryGetValue(table.Name, out var actual));
            Fields(actual, "name", "kind", "persistence", "partition", "typed", "rls", "forceRls", "inherits", "columns");
            Require(Text(actual, "kind") == "r" && Text(actual, "persistence") == "p"
                && !Flag(actual, "partition") && !Flag(actual, "typed") && !Flag(actual, "inherits")
                && !Flag(actual, "rls") && !Flag(actual, "forceRls"));
            var columns = Rows(actual, "columns");
            Require(columns.GetArrayLength() == table.Columns.Count);
            for (var index = 0; index < table.Columns.Count; index++)
            {
                var column = columns[index]; var wanted = table.Columns[index];
                Fields(column, "name", "type", "nullable", "dropped", "identity", "generated", "defaultExpression", "defaultCollation");
                Require(!Flag(column, "dropped") && Text(column, "name") == wanted.Name
                    && Text(column, "type") == wanted.ColumnType && Flag(column, "nullable") == wanted.IsNullable
                    && Text(column, "identity") == "" && Text(column, "generated") == ""
                    && Null(column, "defaultExpression") && Flag(column, "defaultCollation"));
            }
        }
        VerifyConstraints(Rows(root, "constraints"), expected);
        VerifyIndexes(Rows(root, "indexes"), expected);
    }

    private static void VerifyConstraints(JsonElement rows, ExpectedSchema expected)
    {
        var actual = Unique(rows, Key);
        Require(actual.Count == 8);
        foreach (var table in expected.Tables)
        {
            var pk = table.PrimaryKey!;
            var primary = Constraint(actual, table.Name, pk.Name, "p", pk.Columns);
            Require(Null(primary, "principalSchema") && Null(primary, "principalTable")
                && Rows(primary, "principalColumns").GetArrayLength() == 0);
            foreach (var fk in table.ForeignKeys)
            {
                var foreign = Constraint(actual, table.Name, fk.Name, "f", fk.Columns);
                Require(Text(foreign, "principalSchema") == (fk.PrincipalSchema ?? "public")
                    && Text(foreign, "principalTable") == fk.PrincipalTable
                    && Strings(Rows(foreign, "principalColumns")).SequenceEqual(fk.PrincipalColumns!, StringComparer.Ordinal)
                    && Text(foreign, "updateAction") == "a" && Text(foreign, "deleteAction") == "r");
            }
        }
    }

    private static JsonElement Constraint(Dictionary<string, JsonElement> rows, string table,
        string name, string kind, string[] columns)
    {
        Require(rows.TryGetValue(table + "|" + name, out var row));
        Fields(row, "table", "name", "kind", "deferrable", "deferred", "validated", "local", "inheritCount",
            "columns", "principalSchema", "principalTable", "principalColumns", "updateAction", "deleteAction");
        Require(Text(row, "kind") == kind && !Flag(row, "deferrable") && !Flag(row, "deferred")
            && Flag(row, "validated") && Flag(row, "local") && Number(row, "inheritCount") == 0
            && Strings(Rows(row, "columns")).SequenceEqual(columns, StringComparer.Ordinal));
        return row;
    }

    private static void VerifyIndexes(JsonElement rows, ExpectedSchema expected)
    {
        var actual = Unique(rows, Key);
        var indexes = expected.Indexes.Select(index => new ExpectedIndex(index.Table, index.Name,
            index.Columns, index.IsUnique, false, index.Filter)).Concat(expected.Tables.Select(table =>
            new ExpectedIndex(table.Name, table.PrimaryKey!.Name, table.PrimaryKey.Columns, true, true, null))).ToArray();
        Require(actual.Count == 13 && indexes.Select(x => x.Table + "|" + x.Name).Distinct(StringComparer.Ordinal).Count() == 13);
        foreach (var wanted in indexes)
        {
            Require(actual.TryGetValue(wanted.Table + "|" + wanted.Name, out var row));
            Fields(row, "table", "name", "method", "unique", "primary", "valid", "ready", "live", "immediate",
                "nullsNotDistinct", "keyCount", "attributeCount", "expressions", "predicate", "columns",
                "defaultOrdering", "defaultOpclasses", "defaultCollation", "primaryConstraint");
            Require(Text(row, "method") == "btree" && Flag(row, "unique") == wanted.Unique
                && Flag(row, "primary") == wanted.Primary && Flag(row, "valid") && Flag(row, "ready")
                && Flag(row, "live") && Flag(row, "immediate") && !Flag(row, "nullsNotDistinct")
                && !Flag(row, "expressions") && Flag(row, "defaultOrdering") && Flag(row, "defaultOpclasses")
                && Flag(row, "defaultCollation") && Number(row, "keyCount") == wanted.Columns.Length
                && Number(row, "attributeCount") == wanted.Columns.Length
                && Strings(Rows(row, "columns")).SequenceEqual(wanted.Columns, StringComparer.Ordinal));
            Require(wanted.Primary ? Text(row, "primaryConstraint") == wanted.Name : Null(row, "primaryConstraint"));
            Require(wanted.Filter is null ? Null(row, "predicate") : PendingPredicate(Text(row, "predicate")));
        }
    }

    // Only this single source predicate is admitted. The lexer never rewrites SQL string contents.
    private static bool PendingPredicate(string value)
    {
        if (value.Length > 256) return false;
        var tokens = new List<PredicateToken>();
        for (var index = 0; index < value.Length;)
        {
            if (char.IsWhiteSpace(value[index])) { index++; continue; }
            if (tokens.Count >= 64) return false;
            switch (value[index])
            {
                case '(' : tokens.Add(PredicateToken.Open); index++; break;
                case ')' : tokens.Add(PredicateToken.Close); index++; break;
                case '=' : tokens.Add(PredicateToken.Equal); index++; break;
                case ':' when index + 1 < value.Length && value[index + 1] == ':' :
                    tokens.Add(PredicateToken.Cast); index += 2; break;
                case '"' when value.AsSpan(index).StartsWith("\"Status\"", StringComparison.Ordinal):
                    tokens.Add(PredicateToken.Status); index += 8; break;
                case '\'' when value.AsSpan(index).StartsWith("'Pending'", StringComparison.Ordinal):
                    tokens.Add(PredicateToken.Pending); index += 9; break;
                default:
                    var start = index;
                    while (index < value.Length && char.IsAsciiLetter(value[index])) index++;
                    if (start == index) return false;
                    var token = value[start..index].ToLowerInvariant() switch
                    {
                        "text" => PredicateToken.Text,
                        "character" => PredicateToken.Character,
                        "varying" => PredicateToken.Varying,
                        _ => PredicateToken.Invalid
                    };
                    if (token == PredicateToken.Invalid) return false;
                    tokens.Add(token); break;
            }
        }
        try { return new PredicateParser(tokens).Matches(); }
        catch (FormatException) { return false; }
    }

    private sealed class PredicateParser(List<PredicateToken> tokens)
    {
        private int position;
        public bool Matches() => Expression(0) == PredicateKind.Comparison && position == tokens.Count;
        private PredicateKind Expression(int depth)
        {
            var left = Term(depth);
            if (!Take(PredicateToken.Equal)) return left;
            if (left != PredicateKind.Status || Term(depth) != PredicateKind.Pending) throw new FormatException();
            return PredicateKind.Comparison;
        }
        private PredicateKind Term(int depth)
        {
            if (depth > 16) throw new FormatException();
            PredicateKind kind;
            if (Take(PredicateToken.Open))
            {
                kind = Expression(depth + 1);
                if (!Take(PredicateToken.Close)) throw new FormatException();
            }
            else if (Take(PredicateToken.Status)) kind = PredicateKind.Status;
            else if (Take(PredicateToken.Pending)) kind = PredicateKind.Pending;
            else throw new FormatException();
            if (Take(PredicateToken.Cast))
            {
                if (kind == PredicateKind.Comparison) throw new FormatException();
                if (!Take(PredicateToken.Text)
                    && !(Take(PredicateToken.Character) && Take(PredicateToken.Varying))) throw new FormatException();
            }
            return kind;
        }
        private bool Take(PredicateToken token)
        {
            if (position >= tokens.Count || tokens[position] != token) return false;
            position++; return true;
        }
    }

    private static Dictionary<string, JsonElement> Unique(JsonElement rows, Func<JsonElement, string> key)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray()) Require(values.TryAdd(key(row), row));
        return values;
    }
    private static string Key(JsonElement row) => Text(row, "table") + "|" + Text(row, "name");
    private static bool IsPublic(string? schema) => schema is null or "public";
    private static void Fields(JsonElement row, params string[] names)
    {
        Require(row.ValueKind == JsonValueKind.Object);
        var properties = row.EnumerateObject().Select(x => x.Name).ToArray();
        Require(properties.Length == names.Length && properties.Distinct(StringComparer.Ordinal).Count() == names.Length
            && properties.ToHashSet(StringComparer.Ordinal).SetEquals(names));
    }
    private static JsonElement Property(JsonElement row, string name)
    {
        Require(row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out _));
        return row.GetProperty(name);
    }
    private static JsonElement Rows(JsonElement row, string name)
    {
        var value = Property(row, name); Require(value.ValueKind == JsonValueKind.Array); return value;
    }
    private static string[] Strings(JsonElement array) => array.EnumerateArray().Select(value =>
    {
        Require(value.ValueKind == JsonValueKind.String); return value.GetString()!;
    }).ToArray();
    private static string Text(JsonElement row, string name)
    {
        var value = Property(row, name); Require(value.ValueKind == JsonValueKind.String); return value.GetString()!;
    }
    private static bool Flag(JsonElement row, string name)
    {
        var value = Property(row, name); Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False); return value.GetBoolean();
    }
    private static int Number(JsonElement row, string name)
    {
        var value = Property(row, name); Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _)); return value.GetInt32();
    }
    private static bool Null(JsonElement row, string name) => Property(row, name).ValueKind == JsonValueKind.Null;
    private static void Require(bool condition) { if (!condition) throw Rejected(); }
    private static InvalidOperationException Rejected() => new("AUTH_ONBOARDING_SCHEMA_MISMATCH");
    private sealed record ExpectedSchema(CreateTableOperation[] Tables, CreateIndexOperation[] Indexes);
    private sealed record ExpectedIndex(string Table, string Name, string[] Columns, bool Unique, bool Primary, string? Filter);
    private enum PredicateToken { Invalid, Open, Close, Equal, Cast, Status, Pending, Text, Character, Varying }
    private enum PredicateKind { Status, Pending, Comparison }
}
