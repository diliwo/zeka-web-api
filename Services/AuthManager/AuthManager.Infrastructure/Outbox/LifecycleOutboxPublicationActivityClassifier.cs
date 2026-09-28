using System.Text.Json;
using AuthManager.Application.Common.Outbox;
using AuthManager.Core.Lifecycle;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Zeka.Lifecycle.Contracts;

namespace AuthManager.Infrastructure.Outbox;

internal interface IOutboxPublicationActivityClassifier
{
    Task<bool> IsTrustedLifecycleControlAsync(OutboxMessageEnvelope message,
        CancellationToken cancellationToken);
}

internal sealed class LifecycleOutboxPublicationActivityClassifier(AuthDbContext database)
    : IOutboxPublicationActivityClassifier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsTrustedLifecycleControlAsync(OutboxMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        if (message.ContractVersion != LifecycleContractV1.Version
            || message.OrganisationId is not Guid organisationId)
            return false;

        try
        {
            var operationId = message.MessageType switch
            {
                nameof(CloseOrganisationParticipantV1) =>
                    Deserialize<CloseOrganisationParticipantV1>(message.Payload).Header.OperationId,
                nameof(ReleaseOrganisationClosureFenceV1) =>
                    Deserialize<ReleaseOrganisationClosureFenceV1>(message.Payload).Header.OperationId,
                nameof(OrganisationClosingV1) =>
                    Deserialize<OrganisationClosingV1>(message.Payload).Header.OperationId,
                nameof(OrganisationArchivedV1) =>
                    Deserialize<OrganisationArchivedV1>(message.Payload).Header.OperationId,
                _ => Guid.Empty
            };
            if (operationId == Guid.Empty) return false;
            var operation = await database.Set<LifecycleOperation>()
                .IgnoreQueryFilters()
                .Include(x => x.Participants)
                .SingleOrDefaultAsync(x => x.Id == operationId
                    && x.OrganisationId == organisationId
                    && x.Family == LifecycleOperationFamily.Termination, cancellationToken);
            if (operation is null || operation.ClosingAt is null
                || !string.Equals(message.CorrelationId, operation.Id.ToString("D"),
                    StringComparison.Ordinal))
                return false;

            return message.MessageType switch
            {
                nameof(CloseOrganisationParticipantV1) =>
                    ValidateClose(Deserialize<CloseOrganisationParticipantV1>(message.Payload), operation),
                nameof(OrganisationClosingV1) =>
                    ValidateClosing(Deserialize<OrganisationClosingV1>(message.Payload), operation),
                nameof(ReleaseOrganisationClosureFenceV1) => await ValidateRelease(
                    Deserialize<ReleaseOrganisationClosureFenceV1>(message.Payload), operation,
                    cancellationToken),
                nameof(OrganisationArchivedV1) => await ValidateArchived(
                    Deserialize<OrganisationArchivedV1>(message.Payload), operation,
                    cancellationToken),
                _ => false
            };
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static T Deserialize<T>(string payload) =>
        JsonSerializer.Deserialize<T>(payload, Json)
        ?? throw new JsonException("Lifecycle control payload is null.");

    private static bool ValidateClose(CloseOrganisationParticipantV1 command,
        LifecycleOperation operation) =>
        Base(command.Header, operation, 2)
        && command.ClosingAt == operation.ClosingAt
        && command.Header.CausationId == operation.Id
        && command.Header.MessageId == LifecycleMessageIdentityV1.ForPhase(operation.Id,
            command.Header.ParticipantId, "enter-fence", 2);

    private static bool ValidateClosing(OrganisationClosingV1 fact,
        LifecycleOperation operation) =>
        Base(fact.Header, operation, 2)
        && fact.Header.ParticipantId == "auth-management"
        && fact.ClosingAt == operation.ClosingAt
        && fact.RegistryRevision == operation.RegistryRevision
        && fact.InventoryHash == operation.InventoryHash
        && fact.Header.CausationId == operation.Id
        && fact.Header.MessageId == LifecycleMessageIdentityV1.ForPhase(operation.Id,
            "auth-management", "closing-fact", 2);

    private async Task<bool> ValidateRelease(ReleaseOrganisationClosureFenceV1 command,
        LifecycleOperation operation, CancellationToken cancellationToken)
    {
        if (!Base(command.Header, operation, 3)
            || command.Header.MessageId != LifecycleMessageIdentityV1.ForPhase(operation.Id,
                command.Header.ParticipantId, "release-fence", 3))
            return false;
        var receipt = await database.LifecycleClosureFenceReceipts.IgnoreQueryFilters().SingleOrDefaultAsync(x =>
            x.OperationId == operation.Id && x.ParticipantId == command.Header.ParticipantId,
            cancellationToken);
        return receipt is not null
            && command.Header.CausationId == receipt.MessageId
            && command.FenceToken == receipt.FenceToken;
    }

    private async Task<bool> ValidateArchived(OrganisationArchivedV1 fact,
        LifecycleOperation operation, CancellationToken cancellationToken)
    {
        if (!Base(fact.Header, operation, 3)
            || fact.Header.ParticipantId != "auth-management"
            || fact.Header.MessageId != LifecycleMessageIdentityV1.ForPhase(operation.Id,
                "auth-management", "archived-fact", 3)
            || fact.ArchivedAt != operation.ArchivedAt
            || fact.FenceEvidenceHash != operation.ClosureFenceEvidenceHash)
            return false;
        return await database.LifecycleClosureFenceReceipts.IgnoreQueryFilters().AnyAsync(x =>
            x.OperationId == operation.Id && x.MessageId == fact.Header.CausationId,
            cancellationToken);
    }

    private static bool Base(LifecycleMessageHeaderV1 header, LifecycleOperation operation,
        long revision) =>
        header.OperationId == operation.Id
        && header.OrganisationId == operation.OrganisationId
        && header.OperationRevision == revision
        && header.ContractVersion == LifecycleContractV1.Version
        && header.CorrelationId == operation.Id
        && operation.Participants.Any(x => x.Family == LifecycleOperationFamily.Termination
            && x.CapabilityKey == OrganisationClosureCapabilityV1.Fence
            && x.ParticipantId == header.ParticipantId
            && x.ContractVersion == header.ContractVersion);
}
