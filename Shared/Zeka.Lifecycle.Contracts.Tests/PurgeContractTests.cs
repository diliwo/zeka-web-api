using System.Text.Json;
using Xunit;

namespace Zeka.Lifecycle.Contracts.Tests;

public sealed class PurgeContractTests
{
    [Fact]
    public void Versioned_command_binds_exact_item_owner_and_idempotency_identity()
    {
        var planId = Guid.NewGuid();
        var category = "admin-area-document-storage";
        var itemId = $"fixture:{category}";
        var messageId = PurgeCommandV1.CommandId(planId, "admin-area-documents",
            category, itemId);
        var command = new PurgeCommandV1(messageId, PurgeCommandKindV1.ParticipantPurge,
            Guid.NewGuid(), Guid.NewGuid(), 4, Guid.NewGuid(), planId,
            new string('a', 64), Guid.NewGuid(), new string('b', 64),
            "admin-area-documents", "organisation.disposition-category", category,
            itemId, messageId);
        Assert.Equal(command, PurgeCommandV1.Parse(command.CanonicalJson()));
        Assert.Equal(64, command.PayloadHash().Length);
        Assert.Throws<InvalidOperationException>(() => PurgeCommandV1.Parse(
            command.CanonicalJson().Replace(PurgeCommandV1.Version, "zeka-purge-message-v2")));
        Assert.Throws<InvalidOperationException>(() => PurgeCommandV1.Parse(
            (command with { ItemId = "fixture:other" }).CanonicalJson()));
        Assert.Throws<InvalidOperationException>(() => PurgeCommandV1.Parse(
            (command with { MessageId = Guid.NewGuid() }).CanonicalJson()));
        Assert.Throws<InvalidOperationException>(() => PurgeCommandV1.Parse(
            command.CanonicalJson() + " "));
        var receipt = new PurgeParticipantReceiptV1(Guid.NewGuid(), messageId,
            command.OrganisationId, command.TerminationOperationId, command.OperationRevision,
            command.RegistryRevision, command.PlanId, command.PlanHash,
            command.DecisionSetId, command.DecisionSetHash, command.ParticipantId,
            command.CapabilityKey, category, itemId, messageId, PurgeOutcomeV1.Purged,
            new string('c', 64), null);
        Assert.Equal(receipt, JsonSerializer.Deserialize<PurgeParticipantReceiptV1>(
            JsonSerializer.Serialize(receipt)));
    }
}
