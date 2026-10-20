using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Separate diagnostic prerequisite, not PR93 acceptance. The same single test is
/// re-entered in fresh, credential-free vstest children; only the supervisor owns trust.
/// Never execute this test on an operator's Windows account. Discovery is safe.
/// </summary>
public sealed class GatewayHandlerTrustProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_pool_default_validation_fixed_package()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(GatewayHandlerTrustProbeSupport.ChildMode) is not null)
                await GatewayHandlerTrustProbeSupport.RunChildAsync();
            else
                await GatewayHandlerTrustProbeSupport.RunSupervisorAsync(output);
        }
        catch (Exception error)
        {
            // No exception message/inner exception: transport errors can contain targets.
            // Safe detailed receipts are written by the producer, never raw stderr/logs.
            throw new Xunit.Sdk.XunitException("Trust probe failed; inspect sanitized receipts. Error class: " + error.GetType().Name);
        }
    }
}

// Synthetic regressions are deliberately outside the real probe class/filter.
// No certificates, processes, environment mutation or hosted-admission spoofing.
public sealed class GatewayHandlerTrustProbeCleanupTests
{
    [Fact]
    public async Task Primary_failure_survives_successful_cleanup()
    {
        var run = new SyntheticRun();
        var primary = new InvalidOperationException("synthetic-primary");
        run.Body = () => ThrowPrimary(primary);
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(run.ExecuteAsync));
        Assert.Same(primary, run.Failures.PrimaryFailure!.SourceException);
        Assert.Contains(nameof(ThrowPrimary), primary.StackTrace!);
        Assert.Empty(run.Failures.CleanupFailures);
        Assert.Empty(run.Failures.EvidenceFailures);
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Fact]
    public async Task Primary_and_cleanup_failures_remain_separate()
    {
        var run = new SyntheticRun();
        var primary = new InvalidOperationException("synthetic-primary");
        var cleanup = new IOException("synthetic-cleanup");
        run.Body = () => ThrowPrimary(primary);
        run.CleanupError = cleanup;
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(run.ExecuteAsync));
        Assert.Same(cleanup, Assert.Single(run.Failures.CleanupFailures).Error.SourceException);
        Assert.Equal(new[] { "first-cleanup" }, run.ReceiptCleanupStages);
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Fact]
    public async Task Cleanup_failure_alone_cannot_be_erased_by_successful_fallback()
    {
        var run = new SyntheticRun { CleanupError = new IOException("synthetic-cleanup") };
        Assert.Same(run.CleanupError, await Assert.ThrowsAsync<IOException>(run.ExecuteAsync));
        Assert.Null(run.Failures.PrimaryFailure);
        Assert.Single(run.Failures.CleanupFailures);
        Assert.True(run.Receipt!["cleanup-absent"]); // Later fallback/observation succeeded.
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Parent_postcondition_false_or_unverifiable_still_attempts_receipt(bool throws)
    {
        var run = new SyntheticRun { ParentUnchanged = false };
        var observation = new IOException("synthetic-observation");
        if (throws) run.ObservationError = observation;
        var error = await Record.ExceptionAsync(run.ExecuteAsync);
        Assert.NotNull(error);
        Assert.Null(run.Failures.PrimaryFailure);
        var failure = Assert.Single(run.Failures.PostconditionFailures);
        Assert.Equal("parent-environment-unchanged", failure.Stage);
        if (throws)
        {
            Assert.Same(observation, error);
            Assert.Null(run.Receipt!["parent-environment-unchanged"]);
        }
        else Assert.False(run.Receipt!["parent-environment-unchanged"]);
        Assert.Equal(new[] { "parent-environment-unchanged" }, run.ReceiptPostconditionStages);
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipt_sink_failure_is_evidence_limitation_with_or_without_primary(bool withPrimary)
    {
        var run = new SyntheticRun { ReceiptError = new IOException("synthetic-receipt") };
        var primary = new InvalidOperationException("synthetic-primary");
        if (withPrimary) run.Body = () => ThrowPrimary(primary);
        var error = await Record.ExceptionAsync(run.ExecuteAsync);
        Assert.Same(withPrimary ? primary : run.ReceiptError, error);
        Assert.Same(run.ReceiptError, Assert.Single(run.Failures.EvidenceFailures).Error.SourceException);
        Assert.Equal("cleanup-receipt", Assert.Single(run.Failures.EvidenceFailures).Stage);
        Assert.Null(run.Receipt); // Sink failed: no fabricated successful receipt.
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Fact]
    public async Task Completion_sink_failure_cannot_claim_completion()
    {
        var run = new SyntheticRun { CompletionError = new IOException("synthetic-completion") };
        Assert.Same(run.CompletionError, await Assert.ThrowsAsync<IOException>(run.ExecuteAsync));
        Assert.Equal("completion-receipt", Assert.Single(run.Failures.EvidenceFailures).Stage);
        Assert.NotNull(run.Receipt);
        Assert.False(run.Complete);
        Assert.False(run.Failures.CompletionEmitted);
    }

    [Fact]
    public async Task All_failure_categories_are_retained_and_cleanup_continues()
    {
        var run = new SyntheticRun
        {
            CleanupError = new IOException("synthetic-cleanup"),
            ObservationError = new IOException("synthetic-observation"),
            ReceiptError = new IOException("synthetic-receipt")
        };
        var primary = new InvalidOperationException("synthetic-primary");
        run.Body = () => ThrowPrimary(primary);
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(run.ExecuteAsync));
        Assert.Single(run.Failures.CleanupFailures);
        Assert.Single(run.Failures.PostconditionFailures);
        Assert.Single(run.Failures.EvidenceFailures);
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nested_scope_preserves_failure_through_supervisor_cleanup(bool withPrimary)
    {
        var run = new SyntheticRun { CleanupError = new IOException("synthetic-inner-cleanup") };
        var primary = new InvalidOperationException("synthetic-primary");
        if (withPrimary) run.Body = () => ThrowPrimary(primary);
        var attempts = new List<string>();
        var outerCleanup = new IOException("synthetic-outer-cleanup");
        var receiptError = new IOException("synthetic-outer-receipt");
        var error = await Record.ExceptionAsync(() => GatewayHandlerTrustProbeSupport.RunWithCleanupAsync(
            run.Failures, run.ExecuteAsync,
            [new("supervisor-first", () => { attempts.Add("first"); throw outerCleanup; }),
             new("supervisor-second", () => { attempts.Add("second"); return Task.CompletedTask; })],
            [new("supervisor-observed", () => { attempts.Add("observe"); return Task.FromResult(true); })],
            _ => { attempts.Add("receipt"); throw receiptError; }, () => attempts.Add("complete")));
        Assert.Same(withPrimary ? primary : run.CleanupError, error);
        if (!withPrimary) Assert.Null(run.Failures.PrimaryFailure);
        Assert.Equal(2, run.Failures.CleanupFailures.Count);
        Assert.Same(outerCleanup, run.Failures.CleanupFailures[1].Error.SourceException);
        Assert.Same(receiptError, Assert.Single(run.Failures.EvidenceFailures).Error.SourceException);
        Assert.Equal(new[] { "first", "second", "observe", "receipt" }, attempts);
        run.AssertAllAttemptsAndNoCompletion();
    }

    [Fact]
    public async Task Full_success_reports_observed_receipt_then_completion()
    {
        var run = new SyntheticRun();
        await run.ExecuteAsync();
        Assert.False(run.Failures.Failed);
        Assert.True(run.Receipt!["cleanup-absent"]);
        Assert.True(run.Receipt["parent-environment-unchanged"]);
        Assert.Empty(run.ReceiptCleanupStages);
        Assert.Empty(run.ReceiptPostconditionStages);
        Assert.True(run.Complete);
        Assert.True(run.Failures.CompletionEmitted);
        Assert.Equal("complete", run.Attempts.Last());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Task ThrowPrimary(Exception error) => throw error;

    private sealed class SyntheticRun
    {
        internal GatewayHandlerTrustProbeSupport.ProbeFailures Failures { get; } = new();
        internal Func<Task> Body { get; set; } = () => Task.CompletedTask;
        internal Exception? CleanupError { get; init; }
        internal Exception? ObservationError { get; set; }
        internal Exception? ReceiptError { get; init; }
        internal Exception? CompletionError { get; init; }
        internal bool ParentUnchanged { get; init; } = true;
        internal List<string> Attempts { get; } = [];
        internal Dictionary<string, bool?>? Receipt { get; private set; }
        internal string[] ReceiptCleanupStages { get; private set; } = [];
        internal string[] ReceiptPostconditionStages { get; private set; } = [];
        internal bool Complete { get; private set; }

        internal Task ExecuteAsync() => GatewayHandlerTrustProbeSupport.RunWithCleanupAsync(Failures, Body,
            [new("first-cleanup", () =>
                {
                    Attempts.Add("first-cleanup");
                    if (CleanupError is not null) throw CleanupError;
                    return Task.CompletedTask;
                }),
             new("second-cleanup", () => { Attempts.Add("second-cleanup"); return Task.CompletedTask; })],
            [new("parent-environment-unchanged", () =>
                {
                    Attempts.Add("parent-observation");
                    if (ObservationError is not null) throw ObservationError;
                    return Task.FromResult(ParentUnchanged);
                }),
             new("cleanup-absent", () => { Attempts.Add("cleanup-observation"); return Task.FromResult(true); })],
            observed =>
            {
                Attempts.Add("receipt");
                if (ReceiptError is not null) throw ReceiptError;
                Receipt = new(observed);
                ReceiptCleanupStages = Failures.CleanupStages;
                ReceiptPostconditionStages = Failures.PostconditionStages;
            }, () =>
            {
                if (CompletionError is not null) throw CompletionError;
                Attempts.Add("complete"); Complete = true;
            });

        internal void AssertAllAttemptsAndNoCompletion()
        {
            Assert.Equal(new[] { "first-cleanup", "second-cleanup", "parent-observation", "cleanup-observation", "receipt" }, Attempts);
            Assert.True(Failures.Failed);
            Assert.False(Complete);
            Assert.False(Failures.CompletionEmitted);
        }
    }
}
