using Npgsql;
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