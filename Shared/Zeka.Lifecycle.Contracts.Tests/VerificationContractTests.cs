using Xunit;

namespace Zeka.Lifecycle.Contracts.Tests;

public sealed class VerificationContractTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 29, 20, 0, 0, TimeSpan.Zero);

    private static VerifyPurgeCommandV1 Command() => new(Guid.NewGuid(), Guid.NewGuid(),
        Guid.NewGuid(), 7, Guid.NewGuid(), new string('a', 64), Guid.NewGuid(),
        new string('b', 64), Guid.NewGuid(), new string('c', 64), "admin-area-documents",
        "organisation.disposition-category", "admin-area-document-storage",
        "fixture:admin-area-document-storage", "PURGE", At, At.AddMinutes(2));

    private static VerifyPurgeReceiptV1 Receipt(VerifyPurgeCommandV1 command) => new(
        Guid.NewGuid(), command.MessageId, command.Hash(), command.OrganisationId,
        command.TerminationOperationId, command.IrreversibleRevision,
        command.RegistryRevision, command.InventoryHash, command.DecisionSetId,
        command.DecisionSetHash, command.PlanId, command.PlanHash,
        command.ParticipantId, command.CapabilityKey, command.Category, command.ItemId,
        "synthetic-pg-owner-query-v1", At.AddSeconds(1), command.ExpiresAt,
        0, 0, 0, 0, 0, true, new string('d', 64));

    [Fact]
    public void Command_and_receipt_bind_frozen_identity_and_fresh_observation()
    {
        var command = Command();
        var receipt = Receipt(command);
        Assert.Equal(command, VerifyPurgeCommandV1.Parse(command.CanonicalJson()));
        receipt.ValidateAgainst(command, At.AddSeconds(2));
        Assert.Equal(64, command.Hash().Length);
        Assert.Equal(64, receipt.Hash().Length);
        Assert.Contains("historical backups", VerifyPurgeReceiptV1.ProofScope);
        Assert.Throws<InvalidOperationException>(() => VerifyPurgeCommandV1.Parse(
            command.CanonicalJson().Replace(VerifyPurgeCommandV1.Version,
                "zeka-verify-purge-v2")));
        Assert.Throws<InvalidOperationException>(() => VerifyPurgeCommandV1.Parse(
            command.CanonicalJson() + " "));
    }

    [Fact]
    public void Wrong_or_stale_owner_evidence_fails_closed()
    {
        var command = Command();
        var receipt = Receipt(command);
        var now = At.AddSeconds(2);
        Assert.Throws<InvalidOperationException>(() =>
            receipt.ValidateAgainst(command with { OrganisationId = Guid.NewGuid() }, now));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { ParticipantId = "client-management" }).ValidateAgainst(command, now));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { Category = "other" }).ValidateAgainst(command, now));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { IrreversibleRevision = 6 }).ValidateAgainst(command, now));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { CommandMessageId = Guid.NewGuid() }).ValidateAgainst(command, now));
        Assert.Throws<InvalidOperationException>(() =>
            (receipt with { EligibleResidualCount = 1 }).ValidateAgainst(command, now));
        Assert.Throws<InvalidOperationException>(() =>
            receipt.ValidateAgainst(command, command.ExpiresAt));
    }
}
