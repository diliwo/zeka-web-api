using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Zeka.Lifecycle.Contracts.Tests;

public sealed class ClosureContractTests
{
    [Fact]
    public void Lifecycle_time_normalization_is_utc_and_postgresql_microsecond_stable()
    {
        var timestamp = new DateTimeOffset(638946144000000009, TimeSpan.Zero);

        var normalized = LifecycleContractTimeV1.Normalize(timestamp);

        Assert.Equal(new DateTimeOffset(638946144000000000, TimeSpan.Zero), normalized);
        Assert.Equal(normalized, LifecycleContractTimeV1.Normalize(normalized));
        Assert.Throws<ArgumentException>(() => LifecycleContractTimeV1.Normalize(default));
        Assert.Throws<ArgumentException>(() => LifecycleContractTimeV1.Normalize(
            timestamp.ToOffset(TimeSpan.FromHours(1))));
    }

    private static readonly Guid OperationId = Guid.Parse("12000000-0000-0000-0000-000000000001");
    private static readonly Guid OrganisationId = Guid.Parse("22000000-0000-0000-0000-000000000002");
    private static readonly Guid RegistryRevision = Guid.Parse("46020000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset ClosingAt = new(2026, 9, 28, 15, 0, 0, TimeSpan.Zero);
    private const string InventoryHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Command_reply_and_fact_hashes_are_stable_and_identity_bound()
    {
        var header = Header("auth-management", 2);
        var command = new CloseOrganisationParticipantV1(header, ClosingAt);
        var completed = new OrganisationClosureParticipantCompletedV1(
            header, "auth-close-fence", 1, ClosingAt.AddSeconds(1));
        var failed = new OrganisationClosureParticipantFailedV1(header,
            OrganisationClosureParticipantPhaseV1.EnterFence,
            "participant-unavailable", true, ClosingAt.AddSeconds(2));
        var definitelyNotEstablished = new OrganisationClosureParticipantFailedV1(header,
            OrganisationClosureParticipantPhaseV1.EnterFence,
            "participant-unavailable", true, ClosingAt.AddSeconds(2),
            OrganisationClosureBoundaryDispositionV1.NotEstablished);
        var release = new ReleaseOrganisationClosureFenceV1(header, completed.FenceToken);
        var released = new OrganisationClosureFenceReleasedV1(
            header, completed.FenceToken, ClosingAt.AddSeconds(3));
        var closing = new OrganisationClosingV1(
            header, ClosingAt, RegistryRevision, InventoryHash);
        var archived = new OrganisationArchivedV1(
            header, ClosingAt.AddMinutes(1), new string('b', 64));

        new[] { command.PayloadHash, completed.ReceiptHash, failed.ReceiptHash,
                release.PayloadHash, released.ReceiptHash, closing.FactHash, archived.FactHash }
            .Should().OnlyContain(hash => hash.Length == 64);
        OrganisationClosureCapabilityV1.Fence.Should().Be("organisation.closure-fence");
        definitelyNotEstablished.ReceiptHash.Should().NotBe(failed.ReceiptHash);
    }

    [Fact]
    public void Complete_evidence_is_order_independent_and_rejects_missing_or_cross_identity_receipts()
    {
        var auth = Completed("auth-management", 2, ClosingAt.AddSeconds(1));
        var client = Completed("client-management", 2, ClosingAt.AddSeconds(2));
        var requirements = new[]
        {
            new ClosureFenceParticipantRequirementV1("client-management", 1),
            new ClosureFenceParticipantRequirementV1("auth-management", 1)
        };
        var first = new CompleteClosureFenceEvidenceV1(RegistryRevision, InventoryHash,
            requirements, [new(client), new(auth)]);
        var second = new CompleteClosureFenceEvidenceV1(RegistryRevision, InventoryHash,
            requirements.Reverse(), [new(auth), new(client)]);

        first.EvidenceHash.Should().Be(second.EvidenceHash);
        first.Receipts.Select(x => x.ParticipantId)
            .Should().Equal("auth-management", "client-management");

        var missing = () => new CompleteClosureFenceEvidenceV1(RegistryRevision,
            InventoryHash, requirements, [new OrganisationClosureFenceReceiptV1(auth)]);
        var crossed = () => new CompleteClosureFenceEvidenceV1(RegistryRevision,
            InventoryHash, requirements,
            [new OrganisationClosureFenceReceiptV1(auth),
                new OrganisationClosureFenceReceiptV1(Completed("client-management", 2,
                    ClosingAt.AddSeconds(2), Guid.NewGuid()))]);
        missing.Should().Throw<ArgumentException>();
        crossed.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Persisted_receipt_revalidates_hash_and_failure_rejects_free_form_details()
    {
        var receipt = new OrganisationClosureFenceReceiptV1(
            Completed("auth-management", 2, ClosingAt.AddSeconds(1)));
        var json = JsonSerializer.Serialize(receipt);
        JsonSerializer.Deserialize<OrganisationClosureFenceReceiptV1>(json)
            .Should().BeEquivalentTo(receipt);

        var tampered = json.Replace(receipt.ReceiptHash, new string('f', 64), StringComparison.Ordinal);
        var restore = () => JsonSerializer.Deserialize<OrganisationClosureFenceReceiptV1>(tampered);
        restore.Should().Throw<ArgumentException>();

        var unsafeFailure = () => new OrganisationClosureParticipantFailedV1(
            Header("auth-management", 2), OrganisationClosureParticipantPhaseV1.EnterFence,
            "unsafe free form failure detail", true, ClosingAt);
        unsafeFailure.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Phase_message_identity_is_stable_and_separates_participant_phase_and_revision()
    {
        var identity = LifecycleMessageIdentityV1.ForPhase(
            OperationId, "auth-management", "enter-fence", 2);
        LifecycleMessageIdentityV1.ForPhase(OperationId, "auth-management", "enter-fence", 2)
            .Should().Be(identity);
        LifecycleMessageIdentityV1.ForPhase(OperationId, "client-management", "enter-fence", 2)
            .Should().NotBe(identity);
        LifecycleMessageIdentityV1.ForPhase(OperationId, "auth-management", "release-fence", 2)
            .Should().NotBe(identity);
        LifecycleMessageIdentityV1.ForPhase(OperationId, "auth-management", "enter-fence", 3)
            .Should().NotBe(identity);
    }

    private static LifecycleMessageHeaderV1 Header(string participant, long revision,
        Guid? operationId = null) => new(operationId ?? OperationId, OrganisationId, revision,
        participant, LifecycleContractV1.Version, Guid.NewGuid(), Guid.NewGuid(), OperationId);

    private static OrganisationClosureParticipantCompletedV1 Completed(string participant,
        long revision, DateTimeOffset boundary, Guid? operationId = null) => new(
        Header(participant, revision, operationId), $"fence-{participant}", 1, boundary);
}
