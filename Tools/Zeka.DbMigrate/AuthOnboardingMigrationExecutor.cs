using System.Data;
using System.Text.Json.Serialization;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Zeka.OwnerSqlEvidence;
using Zeka.ToolingDiagnostics;

namespace Zeka.DbMigrate;

internal sealed record AuthOnboardingHooks(
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? AfterLock = null,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? AfterGrants = null,
    Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? AfterVerification = null,
    Func<NpgsqlTransaction, CancellationToken, Task>? CommitAsync = null);

internal sealed record AtomicAuthEvidence(
    [property: JsonPropertyName("migrationSqlSha256")] string MigrationSqlSha256,
    [property: JsonPropertyName("grantSqlSha256")] string GrantSqlSha256,
    [property: JsonPropertyName("manifestSha256")] string ManifestSha256,
    [property: JsonPropertyName("commitAttempted")] bool CommitAttempted,
    [property: JsonPropertyName("commitAcknowledged")] bool CommitAcknowledged,
    [property: JsonPropertyName("rollbackConfirmed")] bool? RollbackConfirmed,
    [property: JsonPropertyName("durableState")] string DurableState,
    [property: JsonPropertyName("replayAttempted")] bool ReplayAttempted,
    [property: JsonPropertyName("databaseName")] string? DatabaseName,
    [property: JsonPropertyName("backendPid")] int? BackendPid,
    [property: JsonPropertyName("failedStage")] string? FailedStage);

/// <summary>The exact reviewed Auth migration, grants and full verifier share one transaction.</summary>
internal static class AuthOnboardingMigrationExecutor
{
    internal const string FromMigration = AuthOnboardingSqlArtifacts.FromMigration;
    internal const string TargetMigration = AuthOnboardingSqlArtifacts.TargetMigration;
    // Same database-scoped key as the established migrator; this variant is transaction-owned.
    private const int AdvisoryNamespace = 1515870810;
    // The legacy "latest" alias must not bypass this transaction-only workflow.
    // It is routed here and refused before opening a connection; callers must pin the target.
    internal static bool Handles(CommandOptions options) => options.Operation == MigrationOperation.Apply
        && options.Service.Key == "auth" && (options.Target == TargetMigration || options.Target == "latest");

    internal static async Task<EngineResult> ExecuteAsync(CommandOptions options, string connectionString,
        string sourceCommit, DateTimeOffset startedAt, CancellationToken cancellationToken = default,
        AuthOnboardingHooks? hooks = null)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(2));
        var token = budget.Token;
        var diagnostics = new SafePostgresDiagnosticCapture();
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        DbContext? context = null;
        string? normalizedConnection = null;
        string? databaseName = null;
        int? backendPid = null;
        string? starting = null;
        string? final = null;
        string? sessionUser = null;
        string? currentUser = null;
        string? failedStage = null;
        var restricted = false;
        var membership = false;
        var ownerAssumed = false;
        var locked = false;
        var commitAttempted = false;
        var commitAcknowledged = false;
        bool? rollbackConfirmed = null;
        var durableState = "NOT_CHECKED";
        var stage = "ARTIFACT_BINDING";
        var migration = "not_started";
        var grants = "not_started";
        var manifest = "not_started";
        var classification = "REJECTED";
        var resultCode = "ATOMIC_AUTH_REJECTED";
        var exitCode = ResultCodes.InvalidInput;
        var caveats = new List<string>
        {
            "Only the exact Auth predecessor-to-onboarding SQL and its pinned grants are executable.",
            "bootstrapReconciliation reports only the hash-pinned onboarding grant subset; no full bootstrap reconciliation runs here.",
            "SourceCommit identifies the base; the separately admitted source snapshot and binary hashes bind dirty source.",
            "Provider acceptance and retained-database admission remain separate from source and disposable-test evidence."
        };

        try
        {
            if (options.Operation != MigrationOperation.Apply || options.Service.Key != "auth"
                || options.Target != TargetMigration || options.LockTimeout < TimeSpan.FromSeconds(1)
                || options.LockTimeout > TimeSpan.FromSeconds(60))
                throw new AtomicRefusal("ATOMIC_AUTH_EXACT_TARGET_REQUIRED", ResultCodes.InvalidInput);

            var artifacts = AuthOnboardingSqlArtifacts.Load();
            AuthOnboardingSqlArtifacts.VerifyManifestBinding(typeof(AuthDbContext).Assembly);
            diagnostics.RegisterConnection(connectionString);
            var settings = new NpgsqlConnectionStringBuilder(SafePostgresDiagnostic.WithoutErrorDetail(connectionString))
            { Pooling = false, Multiplexing = false, IncludeErrorDetail = false };
            if (string.IsNullOrWhiteSpace(settings.Database))
                throw new AtomicRefusal("ATOMIC_AUTH_DATABASE_BINDING_REQUIRED", ResultCodes.InvalidInput);
            normalizedConnection = settings.ConnectionString;
            databaseName = settings.Database;
            stage = "CONNECTION";
            connection = new NpgsqlConnection(normalizedConnection);
            await connection.OpenAsync(token);
            backendPid = connection.ProcessID;
            if (connection.PostgreSqlVersion.Major != 17)
                throw new AtomicRefusal("ATOMIC_AUTH_POSTGRESQL17_REQUIRED", ResultCodes.IdentityRejected);

            stage = "TRANSACTION";
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await ConfigureTransactionAsync(connection, transaction, options.LockTimeout, token);
            stage = "ADVISORY_TRANSACTION_LOCK";
            await AcquireTransactionLockAsync(connection, transaction, token);
            locked = true;
            if (hooks?.AfterLock is { } afterLock) await afterLock(connection, transaction, token);

            stage = "ROLE_IDENTITY";
            var identity = await MigrationEngine.ReadIdentityAsync(connection, options.Service, token);
            sessionUser = identity.SessionUser;
            currentUser = identity.CurrentUser;
            restricted = identity.Restricted;
            membership = identity.MatchingMembership;
            if (sessionUser != options.Service.MigratorRole || currentUser != options.Service.MigratorRole
                || !restricted || !membership)
                throw new AtomicRefusal("ATOMIC_AUTH_IDENTITY_REJECTED", ResultCodes.IdentityRejected);
            await AssumeOwnerAsync(connection, transaction, token);
            (sessionUser, currentUser) = await MigrationEngine.ReadUsersAsync(connection, token);
            ownerAssumed = sessionUser == options.Service.MigratorRole && currentUser == options.Service.OwnerRole;
            if (!ownerAssumed)
                throw new AtomicRefusal("ATOMIC_AUTH_OWNER_ASSUMPTION_REJECTED", ResultCodes.IdentityRejected);

            context = MigrationEngine.CreateContext(options.Service, connection, diagnostics);
            await context.Database.UseTransactionAsync(transaction, token);
            stage = "HISTORY_AND_SCHEMA_RECHECK";
            var compiled = context.Database.GetMigrations().ToArray();
            var predecessorIndex = Array.IndexOf(compiled, FromMigration);
            if (predecessorIndex < 0 || predecessorIndex + 2 != compiled.Length || compiled[^1] != TargetMigration)
                throw new AtomicRefusal("ATOMIC_AUTH_COMPILED_HISTORY_REJECTED", ResultCodes.HistoryRejected);
            var history = await ReadHistoryAsync(connection, transaction, token);
            starting = history.LastOrDefault();
            var baseline = compiled.Take(predecessorIndex + 1).ToArray();
            var alreadyCurrent = history.SequenceEqual(compiled, StringComparer.Ordinal);
            if (!alreadyCurrent && !history.SequenceEqual(baseline, StringComparer.Ordinal))
                throw new AtomicRefusal("ATOMIC_AUTH_HISTORY_REJECTED", ResultCodes.HistoryRejected);
            await VerifyBaselineSchemaAsync(connection, transaction, databaseName, alreadyCurrent, token);
            await VerifyDefaultPrivilegesAsync(connection, transaction, token);

            if (!alreadyCurrent)
            {
                stage = "MIGRATION_SQL";
                migration = "started";
                await using (var command = new NpgsqlCommand(artifacts.MigrationSql, connection, transaction))
                    await OwnerSqlExecution.ExecuteNonQueryAsync(command, token);
                migration = "passed";
                stage = "APPROVED_GRANTS";
                grants = "started";
                await using (var command = new NpgsqlCommand(artifacts.GrantSql, connection, transaction))
                    await OwnerSqlExecution.ExecuteNonQueryAsync(command, token);
                grants = "passed";
                if (hooks?.AfterGrants is { } afterGrants) await afterGrants(connection, transaction, token);
            }
            else
            {
                migration = "not_needed";
                grants = "not_needed";
            }

            stage = "EXACT_SCHEMA_VERIFICATION";
            await AuthOnboardingSchemaVerifier.VerifyAsync(context, connection, transaction, token);
            var finalHistory = await ReadHistoryAsync(connection, transaction, token);
            if (!finalHistory.SequenceEqual(compiled, StringComparer.Ordinal))
                throw new AtomicRefusal("ATOMIC_AUTH_FINAL_HISTORY_REJECTED", ResultCodes.HistoryRejected);
            final = finalHistory.LastOrDefault();
            stage = "FULL_V20_VERIFICATION";
            manifest = "started";
            await MigrationEngine.VerifyManifestAsync(context, options.Service.MigrationAssembly, token);
            manifest = "passed";
            if (hooks?.AfterVerification is { } afterVerification)
                await afterVerification(connection, transaction, token);

            stage = "COMMIT";
            commitAttempted = true;
            if (hooks?.CommitAsync is { } commit) await commit(transaction, token);
            else await transaction.CommitAsync(token);
            commitAcknowledged = true;
            durableState = "COMMITTED_VERIFIED";
            classification = alreadyCurrent ? "ATOMIC_AUTH_ALREADY_CURRENT" : "ATOMIC_AUTH_APPLIED";
            resultCode = "SUCCESS";
            exitCode = ResultCodes.Success;
        }
        catch (Exception error)
        {
            diagnostics.Capture(error);
            failedStage = stage;
            if (commitAttempted)
            {
                classification = "ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION";
                resultCode = classification;
                exitCode = ResultCodes.RequiresInspection;
                durableState = "INCONCLUSIVE";
                caveats.Add("Commit submission began; an exception or cancellation cannot establish rollback. No replay was attempted.");
            }
            else
            {
                if (transaction is not null)
                {
                    using var rollbackBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try
                    {
                        await transaction.RollbackAsync(rollbackBudget.Token);
                        rollbackConfirmed = true;
                    }
                    catch (Exception rollbackError)
                    {
                        diagnostics.Capture(rollbackError);
                        rollbackConfirmed = false;
                        caveats.Add("Rollback acknowledgment was unavailable; inspect durable state before any further action.");
                    }
                }
                if (manifest == "started") manifest = "failed";
                if (rollbackConfirmed == true)
                {
                    if (migration is "started" or "passed") migration = "rolled_back";
                    if (grants is "started" or "passed") grants = "rolled_back";
                    durableState = "ROLLED_BACK";
                    classification = "ATOMIC_AUTH_ROLLED_BACK";
                }
                else classification = transaction is null ? "REJECTED" : "ATOMIC_AUTH_REQUIRES_INSPECTION";
                resultCode = error is AtomicRefusal refusal ? refusal.Code : "ATOMIC_AUTH_FAILED";
                exitCode = rollbackConfirmed == false ? ResultCodes.RequiresInspection
                    : error is AtomicRefusal rejected ? rejected.ExitCode : ResultCodes.ManifestRejected;
            }
        }
        finally
        {
            // A committed outcome must never be rewritten as a rollback by a cleanup exception.
            try { if (context is not null) await context.DisposeAsync(); }
            catch { caveats.Add("Context disposal was not acknowledged."); }
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            catch { caveats.Add("Transaction disposal was not acknowledged."); }
            try { if (connection is not null) await connection.DisposeAsync(); }
            catch { caveats.Add("Connection disposal was not acknowledged."); }
        }

        if (commitAttempted && !commitAcknowledged && normalizedConnection is not null)
        {
            durableState = await InspectDurableStateAsync(options, normalizedConnection, diagnostics);
            caveats.Add("Durable inspection is read-only on a fresh connection; inspection does not authorize a retry.");
        }

        var evidence = new MigrationEvidence("zeka-db-migrate-result/v0.1.0", "0.1.0", sourceCommit,
            options.OperationId, "apply", "auth", options.Service.LogicalDatabase, options.Target,
            starting, TargetMigration, final, classification, resultCode, locked,
            new RoleAssertions(options.Service.MigratorRole, sessionUser, options.Service.OwnerRole,
                currentUser, restricted, membership, ownerAssumed),
            new PhaseResults(new PhaseResult(migration), new PhaseResult(grants), new PhaseResult(manifest),
                new PhaseResult("untested")), startedAt, DateTimeOffset.UtcNow, caveats.ToArray())
        {
            PostgresDiagnostic = diagnostics.Diagnostic,
            AtomicAuth = new AtomicAuthEvidence(AuthOnboardingSqlArtifacts.MigrationSha256,
                AuthOnboardingSqlArtifacts.GrantSha256, AuthOnboardingSqlArtifacts.ManifestSha256,
                commitAttempted, commitAcknowledged, rollbackConfirmed, durableState, false,
                databaseName, backendPid, failedStage)
        };
        return new EngineResult(evidence, exitCode);
    }

    private static async Task ConfigureTransactionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        TimeSpan lockTimeout, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.set_config('search_path','public',true),
                   pg_catalog.set_config('statement_timeout','60000',true),
                   pg_catalog.set_config('lock_timeout',@lock_ms,true),
                   pg_catalog.set_config('idle_in_transaction_session_timeout','90000',true)
            """, connection, transaction);
        command.Parameters.AddWithValue("lock_ms", ((int)lockTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        await OwnerSqlExecution.ExecuteNonQueryAsync(command, token);
    }

    private static async Task AcquireTransactionLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.pg_advisory_xact_lock(@namespace,
              CASE WHEN oid::bigint>2147483647 THEN (oid::bigint-4294967296)::integer ELSE oid::integer END)
            FROM pg_catalog.pg_database WHERE datname=pg_catalog.current_database()
            """, connection, transaction);
        command.Parameters.AddWithValue("namespace", AdvisoryNamespace);
        await OwnerSqlExecution.ExecuteNonQueryAsync(command, token);
    }

    private static async Task AssumeOwnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("SET LOCAL ROLE zeka_auth_owner", connection, transaction);
        await OwnerSqlExecution.ExecuteNonQueryAsync(command, token);
    }

    private static async Task<string[]> ReadHistoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT "MigrationId" FROM public."__EFMigrationsHistory" ORDER BY "MigrationId"
            """, connection, transaction);
        await using var reader = await OwnerSqlExecution.ExecuteReaderAsync(command, token);
        var history = new List<string>();
        while (await reader.ReadAsync(token)) history.Add(reader.GetString(0));
        await OwnerSqlExecution.CompleteReaderAsync(reader, token);
        return history.ToArray();
    }

    private static async Task VerifyBaselineSchemaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string database, bool alreadyCurrent, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT pg_catalog.current_database()=@database
              AND EXISTS (
                SELECT 1 FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                JOIN pg_catalog.pg_roles owner ON owner.oid=c.relowner
                JOIN pg_catalog.pg_attribute a ON a.attrelid=c.oid AND a.attname='Id'
                JOIN pg_catalog.pg_constraint pk ON pk.conrelid=c.oid AND pk.contype='p'
                WHERE n.nspname='public' AND c.relname='AspNetUsers' AND c.relkind='r'
                  AND owner.rolname='zeka_auth_owner' AND a.atttypid='uuid'::pg_catalog.regtype
                  AND a.attnotnull AND pk.conkey=ARRAY[a.attnum]::smallint[]),
              (SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
                WHERE n.nspname='public' AND c.relname=ANY(@tables)),
              (SELECT count(*) FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid=t.typnamespace
                WHERE n.nspname='public' AND t.typname=ANY(@tables))
            """, connection, transaction);
        command.Parameters.AddWithValue("database", database);
        command.Parameters.AddWithValue("tables", NewTables);
        await using var reader = await OwnerSqlExecution.ExecuteReaderAsync(command, token);
        if (!await reader.ReadAsync(token)) throw new AtomicRefusal("ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED", ResultCodes.HistoryRejected);
        var valid = reader.GetBoolean(0);
        var relations = reader.GetInt64(1);
        var types = reader.GetInt64(2);
        await OwnerSqlExecution.CompleteReaderAsync(reader, token);
        if (!valid || relations != (alreadyCurrent ? 4 : 0) || types != (alreadyCurrent ? 4 : 0))
            throw new AtomicRefusal("ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED", ResultCodes.HistoryRejected);
    }

    private static async Task VerifyDefaultPrivilegesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT NOT EXISTS (
              SELECT 1 FROM pg_catalog.pg_default_acl d
              JOIN pg_catalog.pg_roles owner ON owner.oid=d.defaclrole
              CROSS JOIN LATERAL pg_catalog.aclexplode(d.defaclacl) acl
              WHERE owner.rolname='zeka_auth_owner' AND acl.grantee<>owner.oid)
            """, connection, transaction);
        if (await OwnerSqlExecution.ExecuteScalarAsync(command, token) is not true)
            throw new AtomicRefusal("ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED", ResultCodes.ManifestRejected);
    }

    private static readonly string[] NewTables =
        ["EmailVerificationChallenges", "OnboardingGrants", "OnboardingOperationReceipts", "VerificationMailOutboxMessages"];

    private static async Task<string> InspectDurableStateAsync(CommandOptions options, string connectionString, SafePostgresDiagnosticCapture diagnostics)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = budget.Token;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await OwnerSqlExecution.ExecuteNonQueryAsync(readOnly, token);
            await ConfigureTransactionAsync(connection, transaction, options.LockTimeout, token);
            await AcquireTransactionLockAsync(connection, transaction, token);
            var identity = await MigrationEngine.ReadIdentityAsync(connection, options.Service, token);
            if (identity.SessionUser != options.Service.MigratorRole || identity.CurrentUser != options.Service.MigratorRole
                || !identity.Restricted || !identity.MatchingMembership) return "INCONCLUSIVE";
            await AssumeOwnerAsync(connection, transaction, token);
            await using var context = MigrationEngine.CreateContext(options.Service, connection);
            await context.Database.UseTransactionAsync(transaction, token);
            var compiled = context.Database.GetMigrations().ToArray();
            var history = await ReadHistoryAsync(connection, transaction, token);
            if (compiled.LastOrDefault() != TargetMigration) return "INCONCLUSIVE";
            if (history.SequenceEqual(compiled, StringComparer.Ordinal))
            {
                await VerifyBaselineSchemaAsync(connection, transaction, connection.Database, true, token);
                await AuthOnboardingSchemaVerifier.VerifyAsync(context, connection, transaction, token);
                await MigrationEngine.VerifyManifestAsync(context, options.Service.MigrationAssembly, token);
                await transaction.RollbackAsync(token);
                return "COMMITTED_VERIFIED";
            }
            if (compiled.Length > 1 && compiled[^2] == FromMigration
                && history.SequenceEqual(compiled[..^1], StringComparer.Ordinal))
            {
                await VerifyBaselineSchemaAsync(connection, transaction, connection.Database, false, token);
                await transaction.RollbackAsync(token);
                return "BASELINE_VERIFIED";
            }
        }
        catch (Exception inspectionError)
        {
            diagnostics.Capture(inspectionError); // Preserve the first safe PostgreSQL diagnostic; never DETAIL.
            // A failed durable observation never proves rollback or authorizes replay.
        }
        return "INCONCLUSIVE";
    }

    private sealed class AtomicRefusal(string code, int exitCode) : Exception(code)
    {
        internal string Code { get; } = code;
        internal int ExitCode { get; } = exitCode;
    }
}
