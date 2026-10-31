using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;

namespace Zeka.LocalStack.DatabaseSetup;

/// <summary>Creates only new, initialization-bound roles; never rotates an existing credential.</summary>
internal static class RoleRegistry
{
    // All services use the cluster-administration database so this lock has one namespace.
    private const long RegistryLock = 0x5A454B414D314442;
    private static readonly string[] Kinds = ["owner", "migrator", "runtime", "closure_recovery"];

    public static async Task<int> EnsureAsync(NpgsqlConnection admin, string initializationId,
        string sourceCommit, string service, IReadOnlyDictionary<string, string> savedPasswords,
        bool allowCreate, CancellationToken ct)
    {
        if (admin.State != ConnectionState.Open || admin.Database != "postgres")
            throw Reject("ROLE_REGISTRY_ADMIN_CONNECTION_REJECTED");
        if (service is not ("auth" or "adminarea" or "client")
            || !Regex.IsMatch(initializationId ?? "", "^[A-Za-z0-9_-]{16,128}$",
                RegexOptions.CultureInvariant)
            || !Regex.IsMatch(sourceCommit ?? "", "^[0-9a-f]{40}$", RegexOptions.CultureInvariant)
            || savedPasswords.Count != 2
            || !savedPasswords.TryGetValue("migrator", out var migratorPassword)
            || !savedPasswords.TryGetValue("runtime", out var runtimePassword)
            || !ValidPassword(migratorPassword) || !ValidPassword(runtimePassword))
            throw Reject("ROLE_REGISTRY_INPUT_REJECTED");

        var specs = Kinds.Select(kind => new RoleSpec(
            $"zeka_{service}_{kind}", kind, kind is "migrator" or "runtime",
            JsonSerializer.Serialize(new
            {
                schema = "zeka-m1-role/v1",
                initializationId,
                service,
                kind,
                sourceCommit
            }))).ToArray();

        try
        {
            await using var transaction = await admin.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using (var command = Command(admin, transaction,
                "SELECT pg_catalog.pg_try_advisory_xact_lock(@lock)"))
            {
                command.Parameters.AddWithValue("lock", RegistryLock);
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
                    throw Reject("ROLE_REGISTRY_LOCK_UNAVAILABLE");
            }

            // Inspect every intended role before any creation. No IF NOT EXISTS adoption.
            var missing = await InspectAsync(admin, transaction, specs, ct).ConfigureAwait(false);
            if (missing.Count != 0 && !allowCreate)
                throw Reject("ROLE_REGISTRY_MISSING_ESTABLISHED_ROLE");

            foreach (var spec in missing)
            {
                var login = spec.Login ? "LOGIN" : "NOLOGIN";
                var credential = spec.Login
                    ? " PASSWORD " + Literal(savedPasswords[spec.Kind])
                    : "";
                var sql = $"CREATE ROLE {Identifier(spec.Name)} {login} NOSUPERUSER " +
                    "NOBYPASSRLS NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION " +
                    "CONNECTION LIMIT -1" + credential + ";";
                await using (var command = Command(admin, transaction, sql))
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await using (var command = Command(admin, transaction,
                    $"COMMENT ON ROLE {Identifier(spec.Name)} IS {Literal(spec.Comment)};"))
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if ((await InspectAsync(admin, transaction, specs, ct).ConfigureAwait(false)).Count != 0)
                throw Reject("ROLE_REGISTRY_POSTCONDITION_REJECTED");
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return missing.Count;
        }
        catch (RoleRegistryFailure) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // SQL used during creation contains a saved credential. Do not preserve
            // provider messages, command text or inner exceptions in a public error.
            throw Reject("ROLE_REGISTRY_OPERATION_REJECTED");
        }
    }

    private static async Task<List<RoleSpec>> InspectAsync(NpgsqlConnection admin,
        NpgsqlTransaction transaction, RoleSpec[] specs, CancellationToken ct)
    {
        const string sql = """
            SELECT r.rolname, r.rolcanlogin, r.rolsuper, r.rolbypassrls,
                   r.rolcreatedb, r.rolcreaterole, r.rolinherit, r.rolreplication,
                   r.rolconnlimit, r.rolconfig IS NULL, r.rolvaliduntil IS NULL,
                   pg_catalog.shobj_description(r.oid, 'pg_authid'),
                   NOT EXISTS (
                       SELECT 1 FROM pg_catalog.pg_db_role_setting s WHERE s.setrole=r.oid)
            FROM pg_catalog.pg_roles r
            WHERE r.rolname=ANY(@roles)
            ORDER BY r.rolname
            """;
        var expected = specs.ToDictionary(spec => spec.Name, StringComparer.Ordinal);
        var observed = new HashSet<string>(StringComparer.Ordinal);
        await using var command = Command(admin, transaction, sql);
        command.Parameters.AddWithValue("roles", specs.Select(spec => spec.Name).ToArray());
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!expected.TryGetValue(name, out var spec) || !observed.Add(name)
                || reader.GetBoolean(1) != spec.Login
                || Enumerable.Range(2, 6).Any(index => reader.GetBoolean(index))
                || reader.GetInt32(8) != -1
                || !reader.GetBoolean(9) || !reader.GetBoolean(10)
                || reader.IsDBNull(11)
                || !string.Equals(reader.GetString(11), spec.Comment, StringComparison.Ordinal)
                || !reader.GetBoolean(12))
                throw Reject("ROLE_REGISTRY_FOREIGN_OR_DRIFTED_ROLE");
        }
        return specs.Where(spec => !observed.Contains(spec.Name)).ToList();
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = 30 };

    private static bool ValidPassword(string? value) =>
        value is not null && Regex.IsMatch(value, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant);

    private static string Identifier(string value) => new NpgsqlCommandBuilder().QuoteIdentifier(value);

    private static string Literal(string value) =>
        "E'" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal) + "'";

    private static RoleRegistryFailure Reject(string code) => new(code);

    private sealed class RoleRegistryFailure(string code) : InvalidOperationException(code);
    private sealed record RoleSpec(string Name, string Kind, bool Login, string Comment);
}
