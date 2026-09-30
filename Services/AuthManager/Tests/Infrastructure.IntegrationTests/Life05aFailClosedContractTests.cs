using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

public sealed class Life05aFailClosedContractTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);
    private static readonly string Hash = new('a', 64);

    private static VerifyPurgeCommandV1 Command() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), 7, Guid.NewGuid(), Hash, Guid.NewGuid(), Hash, Guid.NewGuid(),
        Hash, "admin-area-documents", "organisation.disposition-category",
        "admin-area-document-storage", "fixture:admin-area-document-storage",
        "PURGE", At, At.AddMinutes(1));

    private static VerifyPurgeReceiptV1 Receipt(VerifyPurgeCommandV1 command) => new(
        Guid.NewGuid(), command.MessageId, command.Hash(), command.OrganisationId,
        command.TerminationOperationId, command.IrreversibleRevision,
        command.RegistryRevision, command.InventoryHash, command.DecisionSetId,
        command.DecisionSetHash, command.PlanId, command.PlanHash,
        command.ParticipantId, command.CapabilityKey, command.Category, command.ItemId,
        "synthetic-pg-owner-query-v2", At.AddSeconds(1), command.ExpiresAt,
        0, 0, 0, 0, 0, true, Hash);

    [Fact]
    public void Life05aFailClosed_rejects_cross_operation_participant_category_and_forged_hash()
    {
        var command = Command();
        var receipt = Receipt(command);
        receipt.ValidateAgainst(command, At.AddSeconds(2));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { TerminationOperationId = Guid.NewGuid() })
                .ValidateAgainst(command, At.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { ParticipantId = "other-owner" })
                .ValidateAgainst(command, At.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { Category = "wrong-category" })
                .ValidateAgainst(command, At.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { CommandHash = new string('b', 64) })
                .ValidateAgainst(command, At.AddSeconds(2)));
    }

    [Fact]
    public void Life05aFailClosed_rejects_stale_wrong_version_and_false_document_absence()
    {
        var command = Command();
        var receipt = Receipt(command);
        Assert.Throws<InvalidOperationException>(() =>
            receipt.ValidateAgainst(command, At.AddMinutes(2)));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { FileResidualCount = 1 })
                .ValidateAgainst(command, At.AddSeconds(2)));
        Assert.Throws<InvalidOperationException>(() =>
            VerifyPurgeCommandV1.Parse(command.CanonicalJson()
                .Replace(VerifyPurgeCommandV1.Version, "zeka-verify-purge-v2")));
    }
}
