using FluentAssertions;
using System.Text.Json;
using Xunit;

namespace Zeka.Lifecycle.Contracts.Tests;

public sealed class ExportContractTests
{
    private static readonly Guid OperationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OrganisationId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly Guid RegistryRevision = Guid.Parse("30000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset FenceTime = new(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
    private const string InventoryHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Header_rejects_invalid_identity_revision_and_contract_version()
    {
        var valid = Header("auth");
        valid.ContractVersion.Should().Be(1);

        var create = () => new LifecycleMessageHeaderV1(
            Guid.Empty, OrganisationId, 0, " auth ", 2, Guid.Empty, Guid.Empty, Guid.Empty);
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Complete_fence_evidence_is_canonical_and_order_independent()
    {
        var auth = Entered("auth", 1, FenceTime);
        var client = Entered("client", 2, FenceTime.AddSeconds(1));
        var requirements = new[]
        {
            new ExportFenceParticipantRequirementV1("client", 1),
            new ExportFenceParticipantRequirementV1("auth", 1)
        };

        var first = new CompleteExportFenceEvidenceV1(
            RegistryRevision, InventoryHash, requirements, [new(client), new(auth)]);
        var second = new CompleteExportFenceEvidenceV1(
            RegistryRevision, InventoryHash, requirements.Reverse(), [new(auth), new(client)]);

        first.EvidenceHash.Should().Be(second.EvidenceHash);
        first.Receipts.Select(x => x.ParticipantId).Should().Equal("auth", "client");
        first.EvidenceHash.Should().Be("55a2908392a8b30fbda8f461c2bd5d6dafc1d12b194b50c7a9cf4bc70ad8dc61");
        auth.ReceiptHash.Should().Be("133e822ca3159aa5a948c1eaacf333499e85474d5f4221fdc672a634648788fd");
    }

    [Fact]
    public void Complete_fence_evidence_rejects_missing_duplicate_or_cross_operation_receipts()
    {
        var requirements = new[]
        {
            new ExportFenceParticipantRequirementV1("auth", 1),
            new ExportFenceParticipantRequirementV1("client", 1)
        };
        var auth = new ExportFenceReceiptV1(Entered("auth", 1, FenceTime));
        var otherOperation = new ExportFenceReceiptV1(Entered(
            "client", 2, FenceTime, Guid.Parse("40000000-0000-0000-0000-000000000004")));

        var missing = () => new CompleteExportFenceEvidenceV1(
            RegistryRevision, InventoryHash, requirements, [auth]);
        var duplicate = () => new CompleteExportFenceEvidenceV1(
            RegistryRevision, InventoryHash, requirements, [auth, auth]);
        var crossed = () => new CompleteExportFenceEvidenceV1(
            RegistryRevision, InventoryHash, requirements, [auth, otherOperation]);

        missing.Should().Throw<ArgumentException>();
        duplicate.Should().Throw<ArgumentException>();
        crossed.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Stage_requires_matching_complete_evidence_and_post_fence_snapshot()
    {
        var entered = Entered("auth", 1, FenceTime);
        var evidence = new CompleteExportFenceEvidenceV1(
            RegistryRevision,
            InventoryHash,
            [new ExportFenceParticipantRequirementV1("auth", 1)],
            [new ExportFenceReceiptV1(entered)]);

        var stage = new StageOrganisationExportV1(
            Header("auth"), FenceTime.AddTicks(1), evidence);
        stage.FenceEvidenceHash.Should().Be(evidence.EvidenceHash);
        stage.FenceOwnerParticipantId.Should().Be("auth");

        var sharedFenceStage = new StageOrganisationExportV1(
            Header("auth-documents"), FenceTime.AddTicks(1), evidence, "auth");
        sharedFenceStage.Header.ParticipantId.Should().Be("auth-documents");
        sharedFenceStage.FenceOwnerParticipantId.Should().Be("auth");

        var early = () => new StageOrganisationExportV1(Header("auth"), FenceTime.AddTicks(-1), evidence);
        var unknown = () => new StageOrganisationExportV1(Header("client"), FenceTime.AddTicks(1), evidence);
        var unknownFenceOwner = () => new StageOrganisationExportV1(
            Header("auth-documents"), FenceTime.AddTicks(1), evidence, "client");
        early.Should().Throw<ArgumentException>();
        unknown.Should().Throw<ArgumentException>();
        unknownFenceOwner.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ready_fragment_accepts_only_truthful_dispositions_and_hashes_canonically()
    {
        var included = new ExportCategoryFragmentV1(
            "memberships", ExportCategoryDispositionV1.Included, 2, "memberships-v1",
            new string('b', 64), "fragment:memberships.csv", null);
        var empty = new ExportCategoryFragmentV1(
            "assignments", ExportCategoryDispositionV1.Empty, 0, "assignments-v1",
            new string('c', 64), "fragment:assignments.csv", null);
        var absent = new ExportCategoryFragmentV1(
            "audit-events", ExportCategoryDispositionV1.Withheld, 0, null, null, null,
            "disclosure-policy-unresolved");
        var first = new OrganisationExportFragmentReadyV1(
            Header("auth"), FenceTime.AddMinutes(1), "fence-auth", [included, empty, absent]);
        var second = new OrganisationExportFragmentReadyV1(
            Header("auth"), FenceTime.AddMinutes(1), "fence-auth", [absent, included, empty]);

        first.FragmentHash.Should().Be(second.FragmentHash);
        first.FragmentHash.Should().Be("e198e3e25902349fc49bcc3d9a90d805d5f113028a0aec3860cc2e4aa2956cd9");
        first.Categories.Select(x => x.Category).Should().BeInAscendingOrder(StringComparer.Ordinal);
        Enum.GetNames<ExportCategoryDispositionV1>().Should().NotContain("Blocked");
        first.Categories.Select(x => x.DispositionCode).Should()
            .Equal("empty", "withheld", "included");
    }

    [Fact]
    public void Receipt_round_trip_revalidates_the_canonical_hash()
    {
        var receipt = new ExportFenceReceiptV1(Entered("auth", 1, FenceTime));
        var json = JsonSerializer.Serialize(receipt);

        var restored = JsonSerializer.Deserialize<ExportFenceReceiptV1>(json);

        restored.Should().NotBeNull();
        restored!.ReceiptHash.Should().Be(receipt.ReceiptHash);
        restored.Header.Should().Be(receipt.Header);

        var tampered = json.Replace(receipt.ReceiptHash, new string('f', 64), StringComparison.Ordinal);
        var restoreTampered = () => JsonSerializer.Deserialize<ExportFenceReceiptV1>(tampered);
        restoreTampered.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(ExportCategoryDispositionV1.Included, 0, "schema-v1", true, true, null)]
    [InlineData(ExportCategoryDispositionV1.Empty, 1, "schema-v1", true, true, null)]
    [InlineData(ExportCategoryDispositionV1.NotImplemented, 0, "schema-v1", false, false, "missing")]
    [InlineData(ExportCategoryDispositionV1.Withheld, 0, null, false, false, null)]
    public void Invalid_category_state_is_rejected(
        ExportCategoryDispositionV1 disposition,
        long count,
        string? schema,
        bool withHash,
        bool withArtifact,
        string? reason)
    {
        var create = () => new ExportCategoryFragmentV1(
            "category", disposition, count, schema,
            withHash ? new string('d', 64) : null,
            withArtifact ? "fragment:category.csv" : null,
            reason);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Failure_contract_carries_only_stable_code_and_rejects_free_form_text()
    {
        var failure = new OrganisationExportParticipantFailedV1(
            Header("client"), OrganisationExportParticipantPhaseV1.StageFragment,
            "artifact-provider-unavailable", true, FenceTime);
        failure.FailureCode.Should().Be("artifact-provider-unavailable");

        var unsafeFailure = () => new OrganisationExportParticipantFailedV1(
            Header("client"), OrganisationExportParticipantPhaseV1.StageFragment,
            "unsafe free form failure detail", true, FenceTime);
        unsafeFailure.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Release_contracts_bind_the_exact_fence_token()
    {
        var release = new ReleaseOrganisationExportFenceV1(Header("admin-area"), "fence-admin-1");
        var released = new OrganisationExportFenceReleasedV1(
            Header("admin-area"), release.FenceToken, FenceTime.AddMinutes(2));

        released.FenceToken.Should().Be(release.FenceToken);
        released.ReleasedAt.Should().BeAfter(FenceTime);
    }

    [Fact]
    public void Capability_keys_are_broker_neutral_and_stable()
    {
        OrganisationExportCapabilityV1.Fence.Should().Be("organisation.export-fence");
        OrganisationExportCapabilityV1.Fragment.Should().Be("organisation.export-fragment");
    }

    private static LifecycleMessageHeaderV1 Header(string participant, Guid? operationId = null) => new(
        operationId ?? OperationId,
        OrganisationId,
        1,
        participant,
        LifecycleContractV1.Version,
        Guid.Parse(participant switch
        {
            "auth" => "50000000-0000-0000-0000-000000000005",
            "client" => "60000000-0000-0000-0000-000000000006",
            _ => "70000000-0000-0000-0000-000000000007"
        }),
        Guid.Parse("80000000-0000-0000-0000-000000000008"),
        Guid.Parse("90000000-0000-0000-0000-000000000009"));

    private static OrganisationExportFenceEnteredV1 Entered(
        string participant,
        long fenceRevision,
        DateTimeOffset enteredAt,
        Guid? operationId = null) => new(
            Header(participant, operationId), $"fence-{participant}", fenceRevision, enteredAt);
}
