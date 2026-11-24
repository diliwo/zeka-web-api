using Npgsql;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-27")]
[Trait("Evidence", "PostgreSQL17")]
public sealed class AuthOnboardingAtomicMigrationTests(AuthOnboardingAtomicFixture fixture, ITestOutputHelper output)
    : IClassFixture<AuthOnboardingAtomicFixture>
{
    [Fact]
    public Task Successful_atomic_migration_commits_schema_history_and_exact_grants_after_real_verification()
        => fixture.RunAsync(nameof(Successful_atomic_migration_commits_schema_history_and_exact_grants_after_real_verification), output, async (database, ct) =>
        {
            var verified = 0;
            var result = await database.ExecuteAsync(ct, new AuthOnboardingHooks(
                AfterVerification: (_, _, _) => { Interlocked.Increment(ref verified); return Task.CompletedTask; }));

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("SUCCESS", result.Evidence.ResultCode);
            Assert.Equal("ATOMIC_AUTH_APPLIED", result.Evidence.Classification);
            Assert.Equal(1, verified);
            Assert.NotNull(result.Evidence.AtomicAuth);
            Assert.True(result.Evidence.AtomicAuth!.CommitAttempted);
            Assert.True(result.Evidence.AtomicAuth.CommitAcknowledged);
            Assert.False(result.Evidence.AtomicAuth.ReplayAttempted);
            await database.AssertCommittedAsync(ct);
            await database.VerifyV20Async(ct); // Actual production verifier, independent fresh connection.
        });

    [Fact]
    public Task Failure_during_real_v20_verification_rolls_back_DDL_history_and_grants()
        => fixture.RunAsync(nameof(Failure_during_real_v20_verification_rolls_back_DDL_history_and_grants), output, async (database, ct) =>
        {
            var before = await database.DigestAsync(ct);
            var verified = 0;
            var result = await database.ExecuteAsync(ct, new AuthOnboardingHooks(
                AfterGrants: async (connection, transaction, token) =>
                {
                    // An existing tenant-neutral Identity table is outside the four-table
                    // exact-schema check, so the actual full v20 verifier must reject it.
                    await using var command = new NpgsqlCommand("ALTER TABLE public.\"AspNetUsers\" ENABLE ROW LEVEL SECURITY", connection, transaction);
                    await command.ExecuteNonQueryAsync(token);
                },
                AfterVerification: (_, _, _) => { Interlocked.Increment(ref verified); return Task.CompletedTask; }));

            AssertRolledBack(result);
            Assert.Equal("ATOMIC_AUTH_FAILED", result.Evidence.ResultCode);
            Assert.Equal("FULL_V20_VERIFICATION", result.Evidence.AtomicAuth!.FailedStage);
            Assert.Equal(0, verified);
            Assert.False(result.Evidence.AtomicAuth!.CommitAttempted);
            Assert.Equal(before, await database.DigestAsync(ct));
            await database.AssertTargetAbsentAsync(ct);
        });

    [Fact]
    public Task Missing_required_grant_is_rejected_by_real_verifier_and_rolls_back()
        => fixture.RunAsync(nameof(Missing_required_grant_is_rejected_by_real_verifier_and_rolls_back), output, async (database, ct) =>
        {
            var before = await database.DigestAsync(ct);
            var verified = 0;
            var result = await database.ExecuteAsync(ct, new AuthOnboardingHooks(
                AfterGrants: async (connection, transaction, token) =>
                {
                    await using var command = new NpgsqlCommand("REVOKE SELECT ON public.\"EmailVerificationChallenges\" FROM zeka_auth_runtime", connection, transaction);
                    await command.ExecuteNonQueryAsync(token);
                },
                AfterVerification: (_, _, _) => { Interlocked.Increment(ref verified); return Task.CompletedTask; }));

            AssertRolledBack(result);
            Assert.Equal("ATOMIC_AUTH_FAILED", result.Evidence.ResultCode);
            Assert.Equal(0, verified);
            Assert.False(result.Evidence.AtomicAuth!.CommitAttempted);
            Assert.Equal(before, await database.DigestAsync(ct));
            await database.AssertTargetAbsentAsync(ct);
        });

    [Fact]
    public Task Unexpected_default_privileges_are_rejected_without_committing_target()
        => fixture.RunAsync(nameof(Unexpected_default_privileges_are_rejected_without_committing_target), output, async (database, ct) =>
        {
            await database.AddUnexpectedDefaultGrantAsync(ct);
            var before = await database.DigestAsync(ct);
            var verified = 0;
            var result = await database.ExecuteAsync(ct, new AuthOnboardingHooks(
                AfterVerification: (_, _, _) => { Interlocked.Increment(ref verified); return Task.CompletedTask; }));

            AssertRolledBack(result);
            Assert.Equal("ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED", result.Evidence.ResultCode);
            Assert.Equal(0, verified);
            Assert.False(result.Evidence.AtomicAuth!.CommitAttempted);
            Assert.Equal(before, await database.DigestAsync(ct));
            await database.AssertTargetAbsentAsync(ct);
        });

    [Fact]
    public Task Concurrent_executions_wait_for_the_same_transaction_lock_and_recheck_history()
        => fixture.RunAsync(nameof(Concurrent_executions_wait_for_the_same_transaction_lock_and_recheck_history), output, async (database, ct) =>
        {
            using var pairDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var holder = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var grantExecutions = 0;
            var first = database.ExecuteAsync(pairDeadline.Token, new AuthOnboardingHooks(
                AfterLock: async (connection, _, token) =>
                {
                    holder.TrySetResult(connection.ProcessID);
                    await release.Task.WaitAsync(token);
                },
                AfterGrants: (_, _, _) => { Interlocked.Increment(ref grantExecutions); return Task.CompletedTask; }));
            Task<EngineResult>? second = null;
            try
            {
                var holderPid = await holder.Task.WaitAsync(pairDeadline.Token);
                second = database.ExecuteAsync(pairDeadline.Token, new AuthOnboardingHooks(
                    AfterGrants: (_, _, _) => { Interlocked.Increment(ref grantExecutions); return Task.CompletedTask; }));
                await database.AssertSameAdvisoryLockWaiterAsync(holderPid, pairDeadline.Token);
                Assert.False(second.IsCompleted);
                release.TrySetResult();
                var results = await Task.WhenAll(first, second);
                Assert.All(results, result => Assert.Equal(0, result.ExitCode));
                Assert.Equal("ATOMIC_AUTH_APPLIED", results[0].Evidence.Classification);
                Assert.Equal("ATOMIC_AUTH_ALREADY_CURRENT", results[1].Evidence.Classification);
                Assert.Equal(1, grantExecutions);
                Assert.All(results, result => Assert.False(result.Evidence.AtomicAuth!.ReplayAttempted));
                await database.AssertCommittedAsync(ct);
                await database.VerifyV20Async(ct);
            }
            finally
            {
                pairDeadline.Cancel();
                release.TrySetResult();
                // Bounded cleanup cannot replace the primary assertion or leave an owned attempt running.
                try { await Task.WhenAll(second is null ? [first] : new[] { first, second }).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch { /* Actual results/primary failure are retained by RunAsync. */ }
            }
        });

    [Fact]
    public Task Uncertain_commit_ack_is_inspected_without_any_blind_replay()
        => fixture.RunAsync(nameof(Uncertain_commit_ack_is_inspected_without_any_blind_replay), output, async (database, ct) =>
        {
            var commits = 0;
            var verified = 0;
            var result = await database.ExecuteAsync(ct, new AuthOnboardingHooks(
                AfterVerification: (_, _, _) => { Interlocked.Increment(ref verified); return Task.CompletedTask; },
                CommitAsync: async (transaction, token) =>
                {
                    await transaction.CommitAsync(token); // Real server-side commit before controlled acknowledgment loss.
                    Interlocked.Increment(ref commits);
                    throw new NpgsqlException("CONTROLLED_COMMIT_ACK_LOSS");
                }));

            Assert.Equal(28, result.ExitCode);
            Assert.Equal("ATOMIC_AUTH_COMMIT_REQUIRES_INSPECTION", result.Evidence.ResultCode);
            Assert.NotNull(result.Evidence.AtomicAuth);
            Assert.True(result.Evidence.AtomicAuth!.CommitAttempted);
            Assert.False(result.Evidence.AtomicAuth.CommitAcknowledged);
            Assert.False(result.Evidence.AtomicAuth.ReplayAttempted);
            Assert.Equal("COMMITTED_VERIFIED", result.Evidence.AtomicAuth.DurableState);
            Assert.Equal(1, verified);
            Assert.Equal(1, commits);
            await database.AssertCommittedAsync(ct);
            await database.VerifyV20Async(ct);
            output.WriteLine("Commit acknowledgment loss is controlled fault injection after a real commit; no physical network-loss claim.");
        });

    [Fact]
    public Task Canonical_atomic_and_EF_bootstrap_reference_match_the_full_catalog_at_the_pinned_tool_provider()
        => fixture.RunAsync(nameof(Canonical_atomic_and_EF_bootstrap_reference_match_the_full_catalog_at_the_pinned_tool_provider), output, async (database, ct) =>
        {
            var applied = await database.ExecuteAsync(ct);
            Assert.Equal(0, applied.ExitCode);
            Assert.Equal("ATOMIC_AUTH_APPLIED", applied.Evidence.Classification);
            await database.AssertCommittedAsync(ct);
            await database.VerifyV20Async(ct); // Before any post-target bootstrap could mask a bad atomic grant.
            await database.AssertRuntimeDenialsAsync(ct);
            var canonical = await database.CaptureCatalogAsync(ct);
            var reference = await fixture.CreateDatabaseAsync(ct);
            await reference.PrepareEfBootstrapCompatibilityAsync(ct);
            await reference.AssertCommittedAsync(ct);
            await reference.AssertRuntimeDenialsAsync(ct);
            var compatibility = await reference.CaptureCatalogAsync(ct);
            AssertComparableCatalogs(canonical, compatibility, "8.0.30");
            output.WriteLine(JsonSerializer.Serialize(new { comparison = "atomic-versus-EF-bootstrap",
                providerVersion = "8.0.30", schemaSecurityEqual = true, historyProductVersionDifference = "explicitly-verified",
                canonical.QuerySha256, canonicalRows = canonical.Rows.Length, referenceRows = compatibility.Rows.Length,
                canonicalFullDigest = canonical.Sha256, referenceFullDigest = compatibility.Sha256 }));
        });

    [Fact]
    public Task Predecessor_upgrade_and_acknowledged_second_atomic_execution_preserve_the_complete_catalog()
        => fixture.RunAsync(nameof(Predecessor_upgrade_and_acknowledged_second_atomic_execution_preserve_the_complete_catalog), output, async (database, ct) =>
        {
            await database.AssertTargetAbsentAsync(ct);
            var first = await database.ExecuteAsync(ct);
            Assert.Equal(0, first.ExitCode);
            Assert.Equal("ATOMIC_AUTH_APPLIED", first.Evidence.Classification);
            await database.VerifyV20Async(ct);
            await database.AssertCommittedAsync(ct);
            var committed = await database.CaptureCatalogAsync(ct);
            AssertCanonicalHistory(committed);
            var second = await database.ExecuteAsync(ct);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal("ATOMIC_AUTH_ALREADY_CURRENT", second.Evidence.Classification);
            Assert.Equal("not_needed", second.Evidence.Phases.Migration.Status);
            Assert.Equal("not_needed", second.Evidence.Phases.Bootstrap.Status);
            Assert.True(second.Evidence.AtomicAuth!.CommitAttempted);
            Assert.True(second.Evidence.AtomicAuth.CommitAcknowledged);
            Assert.False(second.Evidence.AtomicAuth.ReplayAttempted);
            Assert.Equal("COMMITTED_VERIFIED", second.Evidence.AtomicAuth.DurableState);
            await database.VerifyV20Async(ct);
            await database.AssertRuntimeDenialsAsync(ct);
            Assert.Equal(committed.Rows, (await database.CaptureCatalogAsync(ct)).Rows);
            var upgrade = await fixture.CreateDatabaseAsync(ct);
            await upgrade.PrepareBaselineAsync(ct); // Persisted exact predecessor before the atomic upgrade.
            await upgrade.AssertTargetAbsentAsync(ct);
            var upgraded = await upgrade.ExecuteAsync(ct);
            Assert.Equal(0, upgraded.ExitCode);
            Assert.Equal("ATOMIC_AUTH_APPLIED", upgraded.Evidence.Classification);
            await upgrade.VerifyV20Async(ct);
            await upgrade.AssertCommittedAsync(ct);
            await upgrade.AssertRuntimeDenialsAsync(ct);
            Assert.Equal(committed.Rows, (await upgrade.CaptureCatalogAsync(ct)).Rows);
        });

    // This operator-orchestrated comparison requires an independently admitted
    // EF8 snapshot; default CI is NOT_EXECUTED. Build explicitly with
    // -p:ZekaOperatorWitnesses=true, then select this exact FullyQualifiedName
    // with dotnet test --no-build --no-restore and the protected snapshot input.
#if ZEKA_OPERATOR_WITNESSES
    [Fact]
    public Task Canonical_atomic_catalog_matches_the_explicit_Auth_EF8_component_reference()
#else
    internal Task Canonical_atomic_catalog_matches_the_explicit_Auth_EF8_component_reference()
#endif
        => fixture.RunAsync(nameof(Canonical_atomic_catalog_matches_the_explicit_Auth_EF8_component_reference), output, async (database, ct) =>
        {
            var path = Environment.GetEnvironmentVariable("ZEKA_AUTH_EF8_CATALOG_SNAPSHOT_FILE");
            if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("AUTH_EF8_COMPONENT_REFERENCE_REQUIRED");
            // Producer evidence needs independent source/binary/TRX admission; hashes do not create trust.
            var bytes = await File.ReadAllBytesAsync(path, ct);
            using (var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                RequireUniqueProperties(document.RootElement);
                Assert.Equal(7, document.RootElement.EnumerateObject().Count());
            }
            var reference = JsonSerializer.Deserialize<AuthCatalogSnapshot>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 })
                ?? throw new InvalidOperationException("AUTH_EF8_COMPONENT_REFERENCE_REJECTED");
            Assert.Equal("Infrastructure.IntegrationTests", reference.ProducerAssembly.Name);
            Assert.Equal("AuthManager.Infrastructure", reference.AuthInfrastructure.Name);
            AssertAssemblyIdentity(reference.ProducerAssembly);
            AssertAssemblyIdentity(reference.AuthInfrastructure);
            AssertAssemblyIdentity(reference.EntityFramework);
            Assert.Equal("Microsoft.EntityFrameworkCore", reference.EntityFramework.Name);
            Assert.InRange(reference.Rows.Length, 1, 20000);
            Assert.All(reference.Rows, row => Assert.True(row is not null && row.Length <= 65536));
            Assert.Equal(0, (await database.ExecuteAsync(ct)).ExitCode);
            await database.VerifyV20Async(ct);
            await database.AssertCommittedAsync(ct);
            await database.AssertRuntimeDenialsAsync(ct);
            var canonical = await database.CaptureCatalogAsync(ct);
            AssertComparableCatalogs(canonical, reference, "8.0.8");
            output.WriteLine(JsonSerializer.Serialize(new { comparison = "tool-EF8.0.30-versus-Auth-EF8.0.8",
                schemaSecurityEqual = true, historyProductVersionDifference = "explicitly-verified",
                canonical.QuerySha256, canonical.EntityFramework, referenceEntityFramework = reference.EntityFramework,
                canonical.AuthInfrastructure, referenceAuthInfrastructure = reference.AuthInfrastructure,
                reference.ProducerAssembly, reference.CapturedAtUtc }));
        });

    private static void RequireUniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                Assert.True(names.Add(property.Name), "Duplicate catalog reference property is not evidence.");
                RequireUniqueProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RequireUniqueProperties(item);
    }

    private static void AssertComparableCatalogs(AuthCatalogSnapshot canonical, AuthCatalogSnapshot reference, string referenceEf)
    {
        Assert.Equal("zeka-auth-catalog/v1", canonical.SchemaVersion);
        Assert.Equal(canonical.SchemaVersion, reference.SchemaVersion);
        Assert.Equal(canonical.QuerySha256, reference.QuerySha256);
        Assert.Equal("8.0.30", canonical.EntityFramework.InformationalVersion.Split('+')[0]);
        Assert.Equal(referenceEf, reference.EntityFramework.InformationalVersion.Split('+')[0]);
        Assert.NotEmpty(canonical.Rows);
        Assert.Equal(canonical.Rows.OrderBy(row => row, StringComparer.Ordinal), canonical.Rows);
        Assert.Equal(reference.Rows.OrderBy(row => row, StringComparer.Ordinal), reference.Rows);
        AssertCanonicalHistory(canonical);
        var expectedHistory = History(canonical);
        var observedHistory = History(reference);
        Assert.Equal(expectedHistory.Select(row => row.Id), observedHistory.Select(row => row.Id));
        Assert.All(observedHistory, row => Assert.Equal(referenceEf, row.ProductVersion));
        // Retain raw history above. ProductVersion is separately verified build provenance;
        // every non-history schema/security row still has to match byte-for-byte.
        Assert.Equal(SecurityRows(canonical), SecurityRows(reference));
    }

    private static void AssertCanonicalHistory(AuthCatalogSnapshot snapshot)
    {
        var history = History(snapshot);
        Assert.True(history.Length >= 2);
        Assert.Equal(AuthOnboardingMigrationExecutor.FromMigration, history[^2].Id);
        Assert.Equal(AuthOnboardingMigrationExecutor.TargetMigration, history[^1].Id);
        Assert.All(history[..^1], row => Assert.Equal("8.0.30", row.ProductVersion));
        // Exact value in the unchanged, hash-bound target SQL artifact.
        Assert.Equal("8.0.8", history[^1].ProductVersion);
    }

    private static (string Id, string ProductVersion)[] History(AuthCatalogSnapshot snapshot)
        => snapshot.Rows.Select(row =>
        {
            using var value = JsonDocument.Parse(row);
            return value.RootElement[0].GetString() == "history"
                ? (value.RootElement[1].GetString()!, value.RootElement[2].GetString()!) : ((string, string)?)null;
        }).OfType<(string Id, string ProductVersion)>().OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();

    private static string[] SecurityRows(AuthCatalogSnapshot snapshot) => snapshot.Rows.Where(row =>
    {
        using var value = JsonDocument.Parse(row);
        return value.RootElement[0].GetString() != "history";
    }).ToArray();

    private static void AssertAssemblyIdentity(AuthCatalogAssemblyIdentity identity)
    {
        Assert.False(string.IsNullOrEmpty(identity.Name));
        Assert.False(string.IsNullOrEmpty(identity.Version));
        Assert.True(Guid.TryParseExact(identity.ModuleVersionId, "D", out _));
        Assert.Equal(64, identity.Sha256.Length);
        Assert.All(identity.Sha256, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    private static void AssertRolledBack(EngineResult result)
    {
        Assert.Equal(27, result.ExitCode);
        Assert.Equal("ATOMIC_AUTH_ROLLED_BACK", result.Evidence.Classification);
        Assert.NotNull(result.Evidence.AtomicAuth);
        Assert.True(result.Evidence.AtomicAuth!.RollbackConfirmed is true);
        Assert.False(result.Evidence.AtomicAuth.ReplayAttempted);
    }
}

// These source-only guards deliberately have no PostgreSQL fixture. The legacy latest
// alias must enter the narrow executor and be refused before any connection is opened.
[Trait("Issue", "ZEK-27")]
[Trait("Evidence", "SourceOnly")]
public sealed class AuthOnboardingAtomicRoutingTests
{
    [Theory]
    [InlineData("auth", "apply", AuthOnboardingMigrationExecutor.TargetMigration, true)]
    [InlineData("auth", "apply", "latest", true)]
    [InlineData("auth", "apply", AuthOnboardingMigrationExecutor.FromMigration, false)]
    [InlineData("client", "apply", "latest", false)]
    [InlineData("auth", "plan", AuthOnboardingMigrationExecutor.TargetMigration, false)]
    public void Routing_captures_exact_Auth_apply_and_latest_without_intercepting_other_operations(
        string serviceKey, string operation, string target, bool expected)
    {
        var options = Options(serviceKey, operation, target);
        Assert.Equal(expected, AuthOnboardingMigrationExecutor.Handles(options));
    }

    [Fact]
    public async Task Latest_is_refused_before_connection_SQL_or_commit()
    {
        var options = Options("auth", "apply", "latest");
        var hooksInvoked = 0;
        var hooks = new AuthOnboardingHooks(
            AfterLock: (_, _, _) => { Interlocked.Increment(ref hooksInvoked); return Task.CompletedTask; },
            AfterGrants: (_, _, _) => { Interlocked.Increment(ref hooksInvoked); return Task.CompletedTask; },
            AfterVerification: (_, _, _) => { Interlocked.Increment(ref hooksInvoked); return Task.CompletedTask; },
            CommitAsync: (_, _) => { Interlocked.Increment(ref hooksInvoked); return Task.CompletedTask; });

        // Actual Git base; the independently recorded dirty snapshot and DLL hashes
        // bind this test candidate. An empty connection string must never be consumed.
        var result = await AuthOnboardingMigrationExecutor.ExecuteAsync(options, string.Empty,
            "4806b71bfcfb168d53592692a935374981106af8", DateTimeOffset.UtcNow, hooks: hooks);

        Assert.Equal(ResultCodes.InvalidInput, result.ExitCode);
        Assert.Equal("ATOMIC_AUTH_EXACT_TARGET_REQUIRED", result.Evidence.ResultCode);
        Assert.NotNull(result.Evidence.AtomicAuth);
        Assert.Equal("ARTIFACT_BINDING", result.Evidence.AtomicAuth!.FailedStage);
        Assert.False(result.Evidence.AtomicAuth.CommitAttempted);
        Assert.False(result.Evidence.AtomicAuth.CommitAcknowledged);
        Assert.False(result.Evidence.AtomicAuth.ReplayAttempted);
        Assert.Null(result.Evidence.AtomicAuth.DatabaseName);
        Assert.Null(result.Evidence.AtomicAuth.BackendPid);
        Assert.Equal(0, hooksInvoked);
    }

    private static CommandOptions Options(string serviceKey, string operation, string target)
    {
        Assert.True(ServiceDescriptor.TryGet(serviceKey, out var service));
        return new CommandOptions(operation == "apply" ? MigrationOperation.Apply : MigrationOperation.Plan,
            service, target, Guid.NewGuid(), Path.GetTempPath(), "routing-source-only.json", TimeSpan.FromSeconds(5));
    }
}
