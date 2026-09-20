using System.Data;
using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using AdminDeploymentDbContext = AdminAreaManagement.Infrastructure.Persistence.DeploymentDbContext;
using AuthDeploymentDbContext = AuthManager.Infrastructure.Persistence.AuthDbContext;
using ClientDeploymentDbContext = ClientManagement.Infrastructure.Persistence.DeploymentDbContext;

namespace Zeka.DbMigrate;

internal sealed record EngineResult(MigrationEvidence Evidence, int ExitCode);

internal static class MigrationEngine
{
    private const int AdvisoryNamespace = 1515870810;

    public static async Task<EngineResult> ExecuteAsync(CommandOptions options, string connectionString,
        string sourceCommit, DateTimeOffset startedAt, CancellationToken cancellationToken = default)
    {
        string? starting = null;
        string? target = null;
        string? final = null;
        var classification = "REJECTED";
        var resultCode = "INTERNAL_FAILURE";
        var lockAcquired = false;
        string? sessionUser = null;
        string? currentUser = null;
        var restricted = false;
        var membership = false;
        var ownerAssumed = false;
        var migration = "not_started";
        var manifest = "not_started";
        var exitCode = ResultCodes.InternalFailure;
        var caveats = new List<string>
        {
            "Bootstrap reconciliation and runtime readiness are separate host phases.",
            "The result is host-produced evidence and is not an independently signed production attestation."
        };

        await using var connection = new NpgsqlConnection(connectionString);
        try
        {
            try { await connection.OpenAsync(cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                resultCode = "CONNECTION_REJECTED";
                exitCode = ResultCodes.ConnectionRejected;
                return Complete();
            }

            lockAcquired = await AcquireLockAsync(connection, options.LockTimeout, cancellationToken);
            if (!lockAcquired)
            {
                resultCode = "LOCK_UNAVAILABLE";
                exitCode = ResultCodes.LockUnavailable;
                return Complete();
            }

            var identity = await ReadIdentityAsync(connection, options.Service, cancellationToken);
            sessionUser = identity.SessionUser;
            currentUser = identity.CurrentUser;
            restricted = identity.Restricted;
            membership = identity.MatchingMembership;
            if (sessionUser != options.Service.MigratorRole || currentUser != options.Service.MigratorRole
                || !restricted || !membership)
            {
                resultCode = "IDENTITY_REJECTED";
                exitCode = ResultCodes.IdentityRejected;
                return Complete();
            }

            await ExecuteFixedAsync(connection,
                $"SET ROLE {new NpgsqlCommandBuilder().QuoteIdentifier(options.Service.OwnerRole)}", cancellationToken);
            (sessionUser, currentUser) = await ReadUsersAsync(connection, cancellationToken);
            ownerAssumed = sessionUser == options.Service.MigratorRole && currentUser == options.Service.OwnerRole;
            if (!ownerAssumed)
            {
                resultCode = "OWNER_ASSUMPTION_REJECTED";
                exitCode = ResultCodes.IdentityRejected;
                return Complete();
            }

            await using var context = CreateContext(options.Service, connection);
            var compiled = context.Database.GetMigrations().ToArray();
            var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
            if (compiled.Length == 0 || applied.Length > compiled.Length
                || !compiled.Take(applied.Length).SequenceEqual(applied, StringComparer.Ordinal))
            {
                resultCode = "MIGRATION_HISTORY_REJECTED";
                exitCode = ResultCodes.HistoryRejected;
                return Complete();
            }
            starting = applied.LastOrDefault();
            target = options.Target == "latest" ? compiled[^1] : compiled.SingleOrDefault(x => x == options.Target);
            if (target is null)
            {
                resultCode = "TARGET_REJECTED";
                exitCode = ResultCodes.HistoryRejected;
                return Complete();
            }

            var currentIndex = applied.Length - 1;
            var targetIndex = Array.IndexOf(compiled, target);
            if (targetIndex < currentIndex)
            {
                classification = "DOWN_TARGET_REJECTED";
                resultCode = classification;
                exitCode = ResultCodes.DownTargetRejected;
                final = starting;
                return Complete();
            }
            if (targetIndex == currentIndex)
            {
                classification = "ALREADY_CURRENT";
                final = starting;
                if (target != compiled[^1])
                {
                    manifest = "untested";
                    caveats.Add("The exact current target predates the manifest migration; final manifest verification is not applicable yet.");
                    resultCode = "SUCCESS";
                    exitCode = ResultCodes.Success;
                }
                else try
                {
                    await VerifyManifestAsync(context, options.Service.MigrationAssembly, cancellationToken);
                    manifest = "passed";
                    resultCode = "SUCCESS";
                    exitCode = ResultCodes.Success;
                }
                catch
                {
                    manifest = "failed";
                    resultCode = "MANIFEST_REJECTED";
                    exitCode = ResultCodes.ManifestRejected;
                }
                return Complete();
            }

            classification = "FORWARD_APPROVED";
            if (options.Operation == MigrationOperation.Plan)
            {
                final = starting;
                resultCode = "SUCCESS";
                exitCode = ResultCodes.Success;
                caveats.Add("Plan mode performed no persistent migration mutation.");
                return Complete();
            }

            try
            {
                migration = "started";
                await context.GetService<IMigrator>().MigrateAsync(target, cancellationToken);
                var observed = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();
                final = observed.LastOrDefault();
                if (final != target)
                    throw new InvalidOperationException("Migration target was not observed after EF returned.");
                migration = "passed";
            }
            catch
            {
                migration = "failed";
                classification = "MIGRATION_STATE_REQUIRES_INSPECTION";
                resultCode = classification;
                exitCode = ResultCodes.RequiresInspection;
                final = await TryReadFinalMigrationAsync(options.Service, connection, cancellationToken);
                caveats.Add("EF migration work started; inspect history and catalog state before any retry.");
                return Complete();
            }

            if (target == compiled[^1])
            {
                try
                {
                    await VerifyManifestAsync(context, options.Service.MigrationAssembly, cancellationToken);
                    manifest = "passed";
                }
                catch
                {
                    manifest = "failed";
                    resultCode = "MANIFEST_REJECTED";
                    exitCode = ResultCodes.ManifestRejected;
                    return Complete();
                }
            }
            else
            {
                manifest = "untested";
                caveats.Add("The exact forward target predates the manifest migration; final manifest verification is not applicable yet.");
            }
            resultCode = "SUCCESS";
            exitCode = ResultCodes.Success;
            return Complete();
        }
        catch (OperationCanceledException)
        {
            if (migration == "started")
            {
                migration = "failed";
                classification = "MIGRATION_STATE_REQUIRES_INSPECTION";
                resultCode = classification;
                exitCode = ResultCodes.RequiresInspection;
            }
            else
            {
                resultCode = "OPERATION_CANCELLED";
                exitCode = ResultCodes.InternalFailure;
            }
            return Complete();
        }
        catch
        {
            resultCode = migration == "started" ? "MIGRATION_STATE_REQUIRES_INSPECTION" : "INTERNAL_FAILURE";
            classification = migration == "started" ? resultCode : classification;
            migration = migration == "started" ? "failed" : migration;
            exitCode = migration == "failed" ? ResultCodes.RequiresInspection : ResultCodes.InternalFailure;
            return Complete();
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                try { await ExecuteFixedAsync(connection, "RESET ROLE", CancellationToken.None); } catch { }
            }
        }

        EngineResult Complete() => new(new MigrationEvidence("zeka-db-migrate-result/v0.1.0", "0.1.0", sourceCommit,
            options.OperationId, options.Operation.ToString().ToLowerInvariant(), options.Service.Key,
            options.Service.LogicalDatabase, options.Target, starting, target, final, classification, resultCode,
            lockAcquired, new RoleAssertions(options.Service.MigratorRole, sessionUser, options.Service.OwnerRole,
                currentUser, restricted, membership, ownerAssumed),
            new PhaseResults(new PhaseResult(migration), new PhaseResult("not_started"), new PhaseResult(manifest),
                new PhaseResult("untested")), startedAt, DateTimeOffset.UtcNow, caveats.ToArray()), exitCode);
    }

    private static DbContext CreateContext(ServiceDescriptor descriptor, NpgsqlConnection connection) => descriptor.Key switch
    {
        "adminarea" => new AdminDeploymentDbContext(
            new DbContextOptionsBuilder<AdminDeploymentDbContext>().UseNpgsql(connection).Options),
        "auth" => new AuthDeploymentDbContext(
            new DbContextOptionsBuilder<AuthDeploymentDbContext>().UseNpgsql(connection).Options),
        "client" => new ClientDeploymentDbContext(
            new DbContextOptionsBuilder<ClientDeploymentDbContext>().UseNpgsql(connection).Options),
        _ => throw new InvalidOperationException()
    };

    private static async Task VerifyManifestAsync(DbContext context, Assembly assembly,
        CancellationToken cancellationToken)
    {
        var verifier = assembly.GetType("Zeka.PersistenceSecurity.RlsSecurityManifestVerifier", true)!;
        var method = verifier.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(x => x.Name == "VerifyAsync"
            && x.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }, _]
            && first == typeof(DbContext) && second == typeof(Assembly));
        var task = (Task)method.Invoke(null, [context, assembly, cancellationToken])!;
        await task.ConfigureAwait(false);
    }

    private static async Task<bool> AcquireLockAsync(NpgsqlConnection connection, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            await using var command = new NpgsqlCommand("""
                SELECT pg_catalog.pg_try_advisory_lock(
                  @namespace,
                  CASE WHEN oid::bigint > 2147483647 THEN (oid::bigint - 4294967296)::integer ELSE oid::integer END)
                FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()
                """, connection);
            command.Parameters.AddWithValue("namespace", AdvisoryNamespace);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return true;
            if (watch.Elapsed >= timeout) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        } while (true);
    }

    private static async Task<(string SessionUser, string CurrentUser, bool Restricted, bool MatchingMembership)>
        ReadIdentityAsync(NpgsqlConnection connection, ServiceDescriptor descriptor, CancellationToken cancellationToken)
    {
        await using var role = new NpgsqlCommand("""
            SELECT session_user,current_user,r.rolcanlogin,r.rolsuper,r.rolbypassrls,r.rolcreatedb,
                   r.rolcreaterole,r.rolinherit,r.rolreplication
            FROM pg_catalog.pg_roles r WHERE r.rolname=session_user
            """, connection);
        await using var reader = await role.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return ("", "", false, false);
        var session = reader.GetString(0);
        var current = reader.GetString(1);
        var restricted = reader.GetBoolean(2) && !reader.GetBoolean(3) && !reader.GetBoolean(4)
            && !reader.GetBoolean(5) && !reader.GetBoolean(6) && !reader.GetBoolean(7) && !reader.GetBoolean(8);
        await reader.DisposeAsync();

        await using var memberships = new NpgsqlCommand("""
            SELECT parent.rolname,m.admin_option,m.inherit_option,m.set_option
            FROM pg_catalog.pg_auth_members m
            JOIN pg_catalog.pg_roles member ON member.oid=m.member
            JOIN pg_catalog.pg_roles parent ON parent.oid=m.roleid
            WHERE member.rolname=@member ORDER BY parent.rolname
            """, connection);
        memberships.Parameters.AddWithValue("member", descriptor.MigratorRole);
        await using var membershipReader = await memberships.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string Parent, bool Admin, bool Inherit, bool Set)>();
        while (await membershipReader.ReadAsync(cancellationToken))
            rows.Add((membershipReader.GetString(0), membershipReader.GetBoolean(1), membershipReader.GetBoolean(2),
                membershipReader.GetBoolean(3)));
        var matching = rows is [{ Parent: var parent, Admin: false, Inherit: false, Set: true }]
            && parent == descriptor.OwnerRole;
        return (session, current, restricted, matching);
    }

    private static async Task<(string SessionUser, string CurrentUser)> ReadUsersAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT session_user,current_user", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task ExecuteFixedAsync(NpgsqlConnection connection, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> TryReadFinalMigrationAsync(ServiceDescriptor descriptor,
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.State != ConnectionState.Open) return null;
            await using var context = CreateContext(descriptor, connection);
            return (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault();
        }
        catch { return null; }
    }
}
