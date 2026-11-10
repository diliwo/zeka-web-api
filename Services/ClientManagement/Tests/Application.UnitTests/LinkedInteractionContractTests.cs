using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.BeneficiaryB1;
using ClientManagement.Application.Common.Authorization;
using MediatR;
using Xunit;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Application.UnitTests;

[Trait("Feature", "B2")]
[Trait("Evidence", "LinkedInteractionContract")]
public sealed class LinkedInteractionContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 30, 0, TimeSpan.Zero);
    private const string Key = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

    [Theory]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("nul")]
    [InlineData("carriage-return")]
    [InlineData("control")]
    [InlineData("delete")]
    [InlineData("next-line")]
    [InlineData("high-surrogate")]
    [InlineData("low-surrogate")]
    [InlineData("broken-pair")]
    public void Purpose_rejects_invalid_text_without_normalizing_it(string scenario)
    {
        var purpose = scenario switch
        {
            "empty" => "",
            "whitespace" => " \t\n",
            "nul" => "a\0b",
            "carriage-return" => "a\rb",
            "control" => "a\u0001b",
            "delete" => "a\u007fb",
            "next-line" => "a\u0085b",
            "high-surrogate" => "\ud800",
            "low-surrogate" => "\udc00",
            _ => "\ud800x"
        };
        InvalidAction(new(purpose, new(2020, 1, 1), Guid.NewGuid()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    public void Purpose_accepts_its_exact_utf16_bounds(int units) =>
        BeneficiaryB1Service.ValidateAction(new(new string('a', units), new(2020, 1, 1), Guid.NewGuid()));

    [Fact]
    public void Purpose_rejects_501_utf16_units() =>
        InvalidAction(new(new string('a', 501), new(2020, 1, 1), Guid.NewGuid()));

    [Fact]
    public void Purpose_counts_surrogate_pairs_as_two_utf16_units()
    {
        var exact = string.Concat(Enumerable.Repeat("\ud83d\ude00", 250));
        Assert.Equal(500, exact.Length);
        BeneficiaryB1Service.ValidateAction(new(exact, new(2020, 1, 1), Guid.NewGuid()));
        InvalidAction(new(exact + "a", new(2020, 1, 1), Guid.NewGuid()));
    }

    [Fact]
    public void Purpose_preserves_whitespace_and_allows_lf_and_tab()
    {
        const string purpose = " \tSynthetic purpose\nwith exact whitespace  ";
        var action = new FollowUpActionInput(purpose, new(2020, 1, 1), Guid.NewGuid());
        BeneficiaryB1Service.ValidateAction(action);
        Assert.Equal(purpose, action.Purpose);
    }

    [Fact]
    public void Null_action_default_date_and_empty_membership_are_invalid()
    {
        InvalidAction(null);
        InvalidAction(new(null!, new(2020, 1, 1), Guid.NewGuid()));
        InvalidAction(new("Synthetic purpose", default, Guid.NewGuid()));
        InvalidAction(new("Synthetic purpose", new(2020, 1, 1), Guid.Empty));
    }

    [Fact]
    public void Past_today_and_nondefault_earliest_dates_are_valid()
    {
        foreach (var date in new[] { new DateOnly(1, 1, 2), new(2020, 1, 1), DateOnly.FromDateTime(Now.UtcDateTime) })
            BeneficiaryB1Service.ValidateAction(new("Synthetic purpose", date, Guid.NewGuid()));
    }

    [Fact]
    public void Typed_assignee_requires_nonempty_guid_not_idempotency_v4_bits()
    {
        var membership = Guid.Parse("bbbbbbbb-bbbb-1bbb-8bbb-bbbbbbbbbbbb");
        BeneficiaryB1Service.ValidateAction(new("Synthetic purpose", new(2020, 1, 1), membership));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Original_note_utf16_bounds_are_preserved(int units) =>
        BeneficiaryB1Service.Validate(1, Now.UtcDateTime, new string('a', units), Key, Now);

    [Fact]
    public void Original_note_rejects_1001_units_and_unpaired_or_nul_text()
    {
        foreach (var note in new[] { new string('a', 1001), "a\0b", "\ud800", "\udc00" })
        {
            var error = Assert.Throws<BeneficiaryWireException>(() =>
                BeneficiaryB1Service.Validate(1, Now.UtcDateTime, note, Key, Now));
            Assert.Equal("INVALID_REQUEST", error.Code);
        }
    }

    [Fact]
    public void Original_note_rules_are_not_strengthened_to_purpose_rules()
    {
        const string note = "Synthetic\u0001note\rwith control text";
        BeneficiaryB1Service.Validate(1, Now.UtcDateTime, note, Key, Now);
        var exact = string.Concat(Enumerable.Repeat("\ud83d\ude00", 500));
        BeneficiaryB1Service.Validate(1, Now.UtcDateTime, exact, Key, Now);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("unspecified")]
    [InlineData("submillisecond")]
    [InlineData("future")]
    public void Original_occurrence_constraints_remain_fail_closed(string scenario)
    {
        var occurred = scenario switch
        {
            "local" => DateTime.SpecifyKind(Now.UtcDateTime, DateTimeKind.Local),
            "unspecified" => DateTime.SpecifyKind(Now.UtcDateTime, DateTimeKind.Unspecified),
            "submillisecond" => Now.UtcDateTime.AddTicks(1),
            _ => Now.UtcDateTime.AddMinutes(5).AddMilliseconds(1)
        };
        var error = Assert.Throws<BeneficiaryWireException>(() =>
            BeneficiaryB1Service.Validate(1, occurred, "Synthetic note", Key, Now));
        Assert.Equal("INVALID_REQUEST", error.Code);
    }

    [Theory]
    [InlineData("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    [InlineData("aaaaaaaa-aaaa-1aaa-8aaa-aaaaaaaaaaaa")]
    [InlineData("aaaaaaaa-aaaa-4aaa-7aaa-aaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa")]
    public void Original_key_constraints_remain_distinct_from_assignee_identity(string key)
    {
        var error = Assert.Throws<BeneficiaryWireException>(() =>
            BeneficiaryB1Service.Validate(1, Now.UtcDateTime, "Synthetic note", key, Now));
        Assert.Equal("INVALID_REQUEST", error.Code);
    }

    [Fact]
    public void Linked_and_original_results_have_separate_minimal_shapes()
    {
        Assert.Equal(new[] { "ContractVersion", "FollowUpActionId", "InteractionId", "Revision" },
            typeof(RecordLinkedInteractionV1).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal(new[] { "ContractVersion", "CreatedAtUtc", "InteractionId", "Revision" },
            typeof(RecordInteractionV1).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray());
    }

    [Fact]
    public async Task New_dynamic_write_authorizes_once_and_resolves_with_authoritative_local_day()
    {
        var f = new Fixture();
        var result = await f.DynamicAsync();
        Assert.Equal(f.Store.Result, result);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(new DateOnly(2026, 10, 26), f.ResolvedDay);
        Assert.Equal(new[] { "caller", "gate", "recover", "timezone", "resolve", "preflight",
            "eligibility", "record", "notify" }, f.Log);
        Assert.Equal(1, f.ResolveCalls);
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.True(f.Store.GuardConsumed);
        Assert.False(f.ActionScope.ConsumeFinalGuard());
        Assert.Equal(f.Membership, f.Store.Actor);
        Assert.Equal(1, f.Publisher.Published);
        Assert.Equal(0, f.Transactions.RetryableCalls);
        Assert.False(f.Transactions.InTransaction);
    }

    [Fact]
    public async Task Fixed_command_keeps_the_supplied_validation_handler_chain_ordinary_once()
    {
        var f = new Fixture();
        var bodyCalls = 0;
        var command = f.Command;
        var result = await f.Executor.ExecuteAsync(command, async token =>
        {
            bodyCalls++;
            return await f.Store.RecordAsync(command, f.Operation.MembershipId, token);
        }, CancellationToken.None);
        Assert.Equal(f.Store.Result, result);
        Assert.Equal(1, bodyCalls);
        Assert.Same(command, f.Store.LastCommand);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.Equal(1, f.Publisher.Published);
        Assert.Equal(0, f.Transactions.RetryableCalls);
    }

    [Fact]
    public async Task EditAll_saved_result_recovers_original_case_without_timezone_or_target_authority()
    {
        var f = new Fixture();
        f.Recovered = f.Command with { SocialCaseId = 3 };
        f.Store.Saved = f.Store.Result;
        f.Authority.TimeZoneFailure = new TenantAccessException(AccessFailure.Unavailable);
        var result = await f.DynamicAsync();
        Assert.Equal(f.Store.Result, result);
        Assert.Equal(new[] { "caller", "gate", "recover", "saved" }, f.Log);
        Assert.Equal(3, f.Store.LastCommand!.SocialCaseId);
        Assert.Equal(0, f.ResolveCalls);
        Assert.Equal(0, f.Authority.ZoneCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Publisher.Published);
        Assert.Equal(1, f.Access.Calls);
    }

    [Fact]
    public async Task Assigned_saved_result_requires_timezone_before_receipt_access()
    {
        var f = new Fixture("Clients.EditAssigned");
        f.Recovered = f.Command;
        f.Store.Saved = f.Store.Result;
        Assert.Equal(f.Store.Result, await f.DynamicAsync());
        Assert.Equal(new[] { "caller", "gate", "timezone", "recover", "saved" }, f.Log);
        Assert.NotNull(f.ActionScope.LocalDay);
        Assert.Equal(1, f.Authority.ZoneCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Assigned_timezone_failure_never_reads_a_saved_receipt()
    {
        var f = new Fixture("Clients.EditAssigned");
        f.Recovered = f.Command;
        f.Store.Saved = f.Store.Result;
        f.Authority.TimeZoneFailure = new TenantAccessException(AccessFailure.Unavailable);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(0, f.RecoverCalls);
        Assert.Equal(0, f.Store.SavedCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
    }

    [Fact]
    public async Task Closed_gate_precedes_every_receipt_or_resource_read()
    {
        var f = new Fixture();
        f.Gate.Closed = true;
        f.Recovered = f.Command;
        f.Store.Saved = f.Store.Result;
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(new[] { "caller", "gate" }, f.Log);
        Assert.Empty(f.Transactions.Methods);
        Assert.Equal(0, f.RecoverCalls);
        Assert.Equal(0, f.Store.SavedCalls);
        Assert.Equal(0, f.Authority.ZoneCalls);
    }

    [Theory]
    [InlineData(TenantAccessOutcome.Denied, AccessFailure.Denied)]
    [InlineData(TenantAccessOutcome.Unavailable, AccessFailure.Unavailable)]
    [InlineData(TenantAccessOutcome.Unauthenticated, AccessFailure.Unauthenticated)]
    public async Task Failed_current_caller_authority_never_reaches_gate_or_receipt(
        TenantAccessOutcome outcome, AccessFailure failure)
    {
        var f = new Fixture();
        f.Access.Decision = new(outcome);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(failure, error.Failure);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(0, f.Gate.Calls);
        Assert.Equal(0, f.RecoverCalls);
        Assert.Throws<TenantContextException>(() => f.Context.Current);
    }

    [Theory]
    [InlineData("Clients.ViewAll")]
    [InlineData("Clients.ViewAssigned")]
    [InlineData("ReferenceData.View")]
    public async Task View_or_selected_organisation_alone_does_not_grant_linked_Edit(string permission)
    {
        var f = new Fixture(permission);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(AccessFailure.Denied, error.Failure);
        Assert.Equal(0, f.Gate.Calls);
        Assert.Equal(0, f.RecoverCalls);
        Assert.Equal(0, f.Store.RecordCalls);
    }

    [Fact]
    public async Task Revoked_caller_is_not_replaced_by_a_cached_saved_result()
    {
        var f = new Fixture();
        f.Recovered = f.Command;
        f.Store.Saved = f.Store.Result;
        await f.DynamicAsync();
        f.Access.Decision = new(TenantAccessOutcome.Denied);
        await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(2, f.Access.Calls);
        Assert.Equal(1, f.Store.SavedCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Theory]
    [InlineData("CONTEXT_REQUIRED")]
    [InlineData("CONTEXT_CONFLICT")]
    public async Task Missing_or_conflicting_case_stops_before_target_details(string code)
    {
        var f = new Fixture();
        f.ResolveFailure = new ActionWriteConflictException(code);
        var error = await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
        Assert.Equal(code, error.Code);
        Assert.Equal(0, f.Store.PreflightCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Dossier_preflight_failure_stops_before_assignee_authority()
    {
        var f = new Fixture();
        f.Store.PreflightFailure = new ActionWriteConflictException("CONTEXT_CONFLICT");
        await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
        Assert.Equal(1, f.Store.PreflightCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Ineligible_target_is_not_retried_or_written()
    {
        var f = new Fixture();
        f.Authority.Decision = null;
        var error = await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
        Assert.Equal("INVALID_ASSIGNEE", error.Code);
        Assert.Equal(1, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Resolved_command_without_action_fails_before_preflight_or_target_authority()
    {
        var f = new Fixture();
        f.Command = f.Command with { Action = null };
        var error = await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
        Assert.Equal("INVALID_ACTION", error.Code);
        Assert.Equal(0, f.Store.PreflightCalls);
        Assert.Equal(0, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
    }

    [Fact]
    public async Task Eligibility_service_failure_propagates_without_retry_or_write()
    {
        var f = new Fixture();
        f.Authority.EligibilityFailure = new TenantAccessException(AccessFailure.Unavailable);
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(1, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Store.ReconcileCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Ambiguous_commit_uses_receipt_only_without_handler_or_authority_replay()
    {
        var f = new Fixture();
        f.Transactions.IndeterminateAfterWrite = true;
        f.Store.Reconciled = f.Store.Result;
        var result = await f.DynamicAsync();
        Assert.Equal(f.Store.Result, result);
        Assert.Equal(1, f.Access.Calls);
        Assert.Equal(1, f.Authority.ZoneCalls);
        Assert.Equal(1, f.Authority.EligibilityCalls);
        Assert.Equal(1, f.ResolveCalls);
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.Equal(1, f.Store.ReconcileCalls);
        Assert.Equal(1, f.Publisher.Published);
        Assert.Equal(0, f.Transactions.RetryableCalls);
        Assert.True(f.Log.IndexOf("reconcile") < f.Log.IndexOf("notify"));
    }

    [Fact]
    public async Task Ambiguous_commit_without_matching_receipt_is_not_success_or_replay()
    {
        var f = new Fixture();
        f.Transactions.IndeterminateAfterWrite = true;
        await Assert.ThrowsAsync<TenantCommitIndeterminateException>(() => f.DynamicAsync());
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.Equal(1, f.Store.ReconcileCalls);
        Assert.Equal(1, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Ordinary_write_failure_does_not_publish_or_reconcile()
    {
        var f = new Fixture();
        f.Store.RecordFailure = new ActionWriteConflictException("SYNTHETIC_WRITE_FAILURE");
        await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.Equal(0, f.Store.ReconcileCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_write_or_replay()
    {
        var f = new Fixture();
        using var stop = new CancellationTokenSource();
        f.Authority.CancelDuringEligibility = stop;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.DynamicAsync(stop.Token));
        Assert.Equal(1, f.Authority.EligibilityCalls);
        Assert.Equal(0, f.Store.RecordCalls);
        Assert.Equal(0, f.Store.ReconcileCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_final_authority_is_rejected_at_the_write_guard(bool staleZone)
    {
        var f = new Fixture();
        if (staleZone) f.Authority.ZoneObserved = Now.AddSeconds(-3);
        else f.Authority.Decision = new("All", "v1", Now.AddSeconds(-3));
        var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
        Assert.Equal(AccessFailure.Unavailable, error.Failure);
        Assert.Equal(1, f.Store.RecordCalls);
        Assert.Equal(0, f.Store.ReconcileCalls);
        Assert.Equal(0, f.Publisher.Published);
    }

    [Theory]
    [InlineData("eligible")]
    [InlineData("ineligible")]
    [InlineData("timezone_failure")]
    [InlineData("eligibility_failure")]
    public async Task Combined_remote_metric_records_only_outcome_without_payload_or_identity(string outcome)
    {
        var f = new Fixture();
        var noteCanary = "synthetic-private-note-" + Guid.NewGuid().ToString("N");
        var purposeCanary = "synthetic-private-purpose-" + Guid.NewGuid().ToString("N");
        f.Command = f.Command with
        {
            Note = noteCanary,
            Action = new(purposeCanary, f.Command.Action!.DueLocalDate, f.Target)
        };
        var prohibited = new[]
        {
            noteCanary, purposeCanary, f.Subject.ToString("D"), f.Organisation.ToString("D"),
            f.Membership.ToString("D"), f.Target.ToString("D"), f.Command.IdempotencyKey
        };
        var observed = new ConcurrentQueue<RemoteMeasurement>();
        var ownExecution = new AsyncLocal<bool>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, subscriber) =>
            {
                if (instrument.Meter.Name == "ClientManagement.ActionAuthority.v1"
                    && instrument.Name == "action_authority.combined_remote_duration_ms")
                    subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (ownExecution.Value)
                observed.Enqueue(new(instrument.Meter.Name, instrument.Name, instrument.Unit,
                    value, tags.ToArray()));
        });
        listener.Start();

        if (outcome == "ineligible") f.Authority.Decision = null;
        if (outcome == "timezone_failure")
            f.Authority.TimeZoneFailure = new TenantAccessException(AccessFailure.Unavailable);
        if (outcome == "eligibility_failure")
            f.Authority.EligibilityFailure = new TenantAccessException(AccessFailure.Unavailable);

        ownExecution.Value = true;
        try
        {
            if (outcome == "eligible")
            {
                var result = await f.DynamicAsync();
                Assert.True(result == f.Store.Result, "The observed executor must complete the linked write.");
                Assert.True(ReferenceEquals(f.Command, f.Store.LastCommand),
                    "The actual write must receive the command carrying the synthetic privacy canaries.");
            }
            else if (outcome == "ineligible")
            {
                var error = await Assert.ThrowsAsync<ActionWriteConflictException>(() => f.DynamicAsync());
                Assert.True(error.Code == "INVALID_ASSIGNEE", "The ineligible execution must fail closed.");
            }
            else
            {
                var error = await Assert.ThrowsAsync<TenantAccessException>(() => f.DynamicAsync());
                Assert.True(error.Failure == AccessFailure.Unavailable,
                    "An authority outage must remain unavailable.");
            }
        }
        finally
        {
            ownExecution.Value = false;
        }

        Assert.True(f.Access.Calls == 1 && f.Authority.ZoneCalls == 1,
            "A real authorized executor path must reach the timezone authority exactly once.");
        Assert.True(f.Authority.EligibilityCalls == (outcome == "timezone_failure" ? 0 : 1),
            "The actual authority sequence must match the measured outcome.");
        Assert.True(f.Store.RecordCalls == (outcome == "eligible" ? 1 : 0),
            "Failed authority must not enter the linked write.");
        Assert.True(f.Publisher.Published == (outcome == "eligible" ? 1 : 0),
            "Only the successful committed path may publish.");
        Assert.True(observed.Count == 1,
            "Privacy evidence requires exactly one actual combined-remote measurement, not an empty capture.");
        Assert.True(observed.TryDequeue(out var measurement), "The actual measurement must be available.");
        Assert.NotNull(measurement);
        Assert.True(IsSafeRemoteMeasurement(measurement!, outcome, prohibited),
            "The real metric must contain only finite duration and its exact harmless outcome tag.");

        // Detector controls use synthetic values and boolean assertions, never value-bearing diagnostics.
        Assert.False(IsSafeRemoteMeasurement(measurement! with
        {
            Tags = [new("outcome", noteCanary)]
        }, outcome, prohibited), "The privacy detector must reject a note-bearing tag.");
        Assert.False(IsSafeRemoteMeasurement(measurement! with
        {
            Tags = [new("outcome", purposeCanary)]
        }, outcome, prohibited), "The privacy detector must reject a purpose-bearing tag.");
        Assert.False(IsSafeRemoteMeasurement(measurement! with
        {
            Tags = [new("outcome", f.Command)]
        }, outcome, prohibited), "The privacy detector must reject retained request objects.");
        Assert.False(IsSafeRemoteMeasurement(measurement! with
        {
            Tags = [new("outcome", outcome), new("request", f.Command)]
        }, outcome, prohibited), "The privacy detector must reject additional structured payload.");
        Assert.False(IsSafeRemoteMeasurement(measurement! with
        {
            InstrumentName = noteCanary
        }, outcome, prohibited), "The privacy detector must reject payload-bearing instrument metadata.");
    }

    private sealed record RemoteMeasurement(string MeterName, string InstrumentName, string? Unit,
        double Value, KeyValuePair<string, object?>[] Tags);

    private static bool IsSafeRemoteMeasurement(RemoteMeasurement measurement, string outcome,
        IEnumerable<string> prohibited)
    {
        if (measurement.MeterName != "ClientManagement.ActionAuthority.v1"
            || measurement.InstrumentName != "action_authority.combined_remote_duration_ms"
            || measurement.Unit != "ms" || !double.IsFinite(measurement.Value) || measurement.Value < 0
            || measurement.Tags.Length != 1)
            return false;
        var tag = measurement.Tags[0];
        return tag.Key == "outcome" && tag.Value is string value && value == outcome
            && !prohibited.Any(item => tag.Key.Contains(item, StringComparison.Ordinal)
                || value.Contains(item, StringComparison.Ordinal));
    }

    private static void InvalidAction(FollowUpActionInput? action)
    {
        var error = Assert.Throws<BeneficiaryWireException>(() => BeneficiaryB1Service.ValidateAction(action));
        Assert.Equal("INVALID_REQUEST", error.Code);
    }

    private sealed class Fixture
    {
        public readonly List<string> Log = [];
        public readonly Guid Organisation = Guid.NewGuid();
        public readonly Guid Membership = Guid.NewGuid();
        public readonly Guid Subject = Guid.NewGuid();
        public readonly Guid Target = Guid.NewGuid();
        public readonly TenantContextScope Context = new();
        public readonly ActionWriteAuthorityScope ActionScope = new();
        public readonly Transactions Transactions;
        public readonly Access Access;
        public readonly Gate Gate;
        public readonly Authority Authority;
        public readonly Store Store;
        public readonly NotificationPublisher Publisher;
        public readonly TenantOperation Operation;
        public readonly LinkedInteractionExecutor Executor;
        public RecordInteractionCommand Command;
        public RecordInteractionCommand? Recovered;
        public DateOnly? ResolvedDay;
        public int RecoverCalls;
        public int ResolveCalls;
        public Exception? ResolveFailure;

        public Fixture(string permission = "Clients.EditAll")
        {
            Transactions = new();
            Access = new(Log, Transactions, new(TenantAccessOutcome.Authorized,
                new(Organisation, Membership, [permission], "v1", Now)));
            Operation = new(Access, new Identity(Subject.ToString("D"), Organisation), Context, Context, ActionScope);
            Gate = new(Log);
            Authority = new(Log, Transactions, Organisation, Subject, Membership, Target);
            var mediator = DispatchProxy.Create<IMediator, NotificationPublisher>();
            Publisher = (NotificationPublisher)(object)mediator;
            Publisher.Log = Log;
            Publisher.TransactionIsActive = () => Transactions.InTransaction;
            var notifications = new PostCommitNotifications(mediator);
            Store = new(Log, Transactions, notifications, ActionScope, Target);
            Executor = new(Operation, Transactions, notifications, Store, Authority, Gate, ActionScope, new Clock());
            Command = new(7, 17, Now.UtcDateTime, "Synthetic interaction", Key,
                new("Synthetic purpose", new(2026, 10, 26), Target));
        }

        public Task<RecordInteractionResult> DynamicAsync(CancellationToken token = default) =>
            Executor.ExecuteAsync(t =>
            {
                Assert.True(Transactions.InTransaction);
                t.ThrowIfCancellationRequested();
                RecoverCalls++;
                Log.Add("recover");
                return Task.FromResult(Recovered);
            }, (day, t) =>
            {
                Assert.True(Transactions.InTransaction);
                t.ThrowIfCancellationRequested();
                ResolveCalls++;
                ResolvedDay = day;
                Log.Add("resolve");
                if (ResolveFailure is not null) throw ResolveFailure;
                return Task.FromResult(Command);
            }, token);
    }

    private sealed record Identity(string SubjectId, Guid SelectedOrganisationId) : IOperationIdentity;
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class Access(List<string> log, Transactions transactions, TenantAccessDecision decision)
        : ICurrentTenantAccess
    {
        public TenantAccessDecision Decision = decision;
        public int Calls;
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid organisation, CancellationToken token)
        {
            Assert.False(transactions.InTransaction);
            token.ThrowIfCancellationRequested();
            Calls++;
            log.Add("caller");
            return Task.FromResult(Decision);
        }
    }

    private sealed class Gate(List<string> log) : IActionWriteActivationGate
    {
        public bool Closed;
        public int Calls;
        public void DemandActionWrite()
        {
            Calls++;
            log.Add("gate");
            if (Closed) throw new TenantAccessException(AccessFailure.Unavailable);
        }
    }

    private sealed class Authority(List<string> log, Transactions transactions, Guid organisation,
        Guid subject, Guid membership, Guid expectedTarget) : IActionAuthorityReads
    {
        public int ZoneCalls;
        public int EligibilityCalls;
        public Exception? TimeZoneFailure;
        public Exception? EligibilityFailure;
        public DateTimeOffset ZoneObserved = Now;
        public AssigneeDecision? Decision = new("All", "v1", Now);
        public CancellationTokenSource? CancelDuringEligibility;

        public Task<ActionTimeZone> TimeZoneAsync(Guid org, Guid actor, Guid member, CancellationToken token)
        {
            Assert.False(transactions.InTransaction);
            Assert.Equal(organisation, org);
            Assert.Equal(subject, actor);
            Assert.Equal(membership, member);
            token.ThrowIfCancellationRequested();
            ZoneCalls++;
            log.Add("timezone");
            if (TimeZoneFailure is not null) throw TimeZoneFailure;
            var zone = TimeZoneInfo.CreateCustomTimeZone("Pacific/Kiritimati", TimeSpan.FromHours(14),
                "Synthetic organisation zone", "Synthetic organisation zone");
            return Task.FromResult(new ActionTimeZone("Pacific/Kiritimati", 1, ZoneObserved, zone));
        }

        public Task<AssigneeDecision?> EligibilityAsync(Guid org, Guid actor, Guid member, Guid target,
            CancellationToken token)
        {
            Assert.False(transactions.InTransaction);
            Assert.Equal(organisation, org);
            Assert.Equal(subject, actor);
            Assert.Equal(membership, member);
            Assert.Equal(expectedTarget, target);
            EligibilityCalls++;
            log.Add("eligibility");
            if (EligibilityFailure is not null) throw EligibilityFailure;
            CancelDuringEligibility?.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(Decision);
        }
    }

    private sealed class Store(List<string> log, Transactions transactions, PostCommitNotifications notifications,
        ActionWriteAuthorityScope authority, Guid target) : IActionWriteStore
    {
        public readonly RecordInteractionResult Result = new(101, 1, 202);
        public RecordInteractionResult? Saved;
        public RecordInteractionResult? Reconciled;
        public RecordInteractionCommand? LastCommand;
        public int SavedCalls;
        public int PreflightCalls;
        public int RecordCalls;
        public int ReconcileCalls;
        public Guid Actor;
        public bool GuardConsumed;
        public Exception? PreflightFailure;
        public Exception? RecordFailure;

        public Task<RecordInteractionResult?> TrySavedResultAsync(RecordInteractionCommand request,
            Guid actorMembershipId, CancellationToken token)
        {
            Assert.True(transactions.InTransaction);
            token.ThrowIfCancellationRequested();
            SavedCalls++;
            LastCommand = request;
            Actor = actorMembershipId;
            log.Add("saved");
            return Task.FromResult(Saved);
        }

        public Task<ActionPreflight> PreflightAsync(RecordInteractionCommand request, CancellationToken token)
        {
            Assert.True(transactions.InTransaction);
            token.ThrowIfCancellationRequested();
            PreflightCalls++;
            log.Add("preflight");
            if (PreflightFailure is not null) throw PreflightFailure;
            return Task.FromResult(new ActionPreflight(request.ClientId, request.SocialCaseId));
        }

        public Task<RecordInteractionResult> RecordAsync(RecordInteractionCommand request, Guid actorMembershipId,
            CancellationToken token)
        {
            Assert.True(transactions.InTransaction);
            token.ThrowIfCancellationRequested();
            RecordCalls++;
            LastCommand = request;
            Actor = actorMembershipId;
            log.Add("record");
            GuardConsumed = authority.ConsumeFinalGuard();
            Assert.True(GuardConsumed);
            authority.DemandTarget(target);
            authority.ValidateFinal(Now);
            transactions.WriteReached = true;
            if (RecordFailure is not null) throw RecordFailure;
            notifications.Enqueue(new Recorded());
            return Task.FromResult(Result);
        }

        public Task<RecordInteractionResult?> ReconcileAsync(RecordInteractionCommand request,
            Guid actorMembershipId, CancellationToken token)
        {
            Assert.True(transactions.InTransaction);
            token.ThrowIfCancellationRequested();
            ReconcileCalls++;
            LastCommand = request;
            Actor = actorMembershipId;
            log.Add("reconcile");
            return Task.FromResult(Reconciled);
        }

        public Task<ActionChangeResult?> TrySavedResultAsync(ActionChangeCommand request,
            Guid actorMembershipId, CancellationToken token) => throw new NotSupportedException();
        public Task<ActionPreflight> PreflightAsync(ActionChangeCommand request,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ActionChangeResult> ChangeAsync(ActionChangeCommand request, Guid actorMembershipId,
            CancellationToken token) => throw new NotSupportedException();
        public Task<ActionChangeResult?> ReconcileAsync(ActionChangeCommand request,
            Guid actorMembershipId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Transactions : ITenantTransactionExecutor
    {
        public bool InTransaction;
        public bool WriteReached;
        public bool IndeterminateAfterWrite;
        public bool CommitExceptionThrown;
        public int RetryableCalls;
        public readonly List<string> Methods = [];

        private async Task<T> Run<T>(string method, Func<CancellationToken, Task<T>> work, CancellationToken token)
        {
            Assert.False(InTransaction);
            Methods.Add(method);
            InTransaction = true;
            T result;
            try { result = await work(token); }
            finally { InTransaction = false; }
            if (method == "ordinary-once" && WriteReached && IndeterminateAfterWrite && !CommitExceptionThrown)
            {
                CommitExceptionThrown = true;
                throw new TenantCommitIndeterminateException(new InvalidOperationException("Synthetic commit uncertainty"));
            }
            return result;
        }

        public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            Run("once", work, token);
        public Task<T> ExecuteOrdinaryOnceAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            Run("ordinary-once", work, token);
        public Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            Run("ordinary", work, token);
        public Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            Run("ordinary", async t => { await work(t); return true; }, token);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token)
        { RetryableCalls++; throw new InvalidOperationException("Retryable transaction path is not part of linked writes."); }
        public Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken token)
        { RetryableCalls++; throw new InvalidOperationException("Retryable transaction path is not part of linked writes."); }
        public Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken token) =>
            throw new InvalidOperationException("Lifecycle path is not part of linked writes.");
        public Task ExecuteLifecycleAsync(Func<CancellationToken, Task> work, CancellationToken token) =>
            throw new InvalidOperationException("Lifecycle path is not part of linked writes.");
    }

    private sealed record Recorded : INotification;

    public class NotificationPublisher : DispatchProxy
    {
        public int Published;
        internal List<string> Log = [];
        internal Func<bool> TransactionIsActive = () => throw new InvalidOperationException("Transaction observation is required.");

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "Publish") throw new InvalidOperationException("Nested MediatR dispatch is forbidden.");
            Assert.False(TransactionIsActive());
            Published++;
            Log.Add("notify");
            return Task.CompletedTask;
        }
    }
}
