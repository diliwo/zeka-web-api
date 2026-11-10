using System.Globalization;
using System.Text;
using System.Text.Json;
using ClientManagement.Application.ActionWrites;
using ClientManagement.Application.BeneficiaryB1;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClientManagement.Infrastructure.Persistence;

internal sealed class BeneficiaryB1Store(ApplicationDbContext database, IClientParentLock locks,
    IActionWriteStore actionWrites) : IBeneficiaryB1Store
{
    private const string RecordOperation = "RecordInteraction.v1";
    private const string AudienceText = "Current authorised dossier readers may read this interaction history.";

    public async Task<BeneficiaryContextV1> ContextAsync(int beneficiaryId, bool canEditAll,
        bool canEditAssigned, Guid actorMembershipId, CancellationToken token)
    {
        var client = await DemandDossierAsync(beneficiaryId, token);
        var day = DateTime.Today;
        var cases = await database.SocialCases.AsNoTracking().Where(x => x.ClientId == beneficiaryId)
            .Where(CurrentAssignment.ActiveOn(day)).Take(2).ToListAsync(token);
        var state = cases.Count switch
        {
            0 => "None",
            > 1 => "Ambiguous",
            _ => await database.SocialWorkers.AnyAsync(w => w.Id == cases[0].SocialWorkerId
                && !w.Softdelete && w.OrganisationMembershipId != Guid.Empty, token) ? "One" : "Invalid"
        };
        var canEditDossier = canEditAll || canEditAssigned
            && await database.AssignedClients(actorMembershipId).AnyAsync(c => c.Id == beneficiaryId, token);
        return new BeneficiaryContextV1(1, beneficiaryId,
            string.Join(' ', new[] { client.FirstName, client.LastName }.Where(x => !string.IsNullOrWhiteSpace(x))),
            state, new AudienceV1("CurrentAuthorisedDossierReaders", AudienceText),
            canEditDossier && state == "One");
    }

    public async Task<InteractionPageV1> RecentAsync(int beneficiaryId, int limit,
        string? cursor, CancellationToken token)
    {
        await DemandDossierAsync(beneficiaryId, token);
        if (limit is < 1 or > 50) throw new BeneficiaryWireException("INVALID_REQUEST");
        (DateTime OccurredAtUtc, int InteractionId)? position = cursor is null
            ? null : DecodeCursor(cursor, beneficiaryId);
        var query = database.Visible<Interaction>().AsNoTracking().Where(x => x.ClientId == beneficiaryId);
        if (position is not null)
            query = query.Where(x => x.OccurredAtUtc < position.Value.OccurredAtUtc
                || x.OccurredAtUtc == position.Value.OccurredAtUtc && x.Id < position.Value.InteractionId);
        var rows = await query.OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id)
            .Take(limit + 1).ToListAsync(token);
        var more = rows.Count > limit;
        if (more) rows.RemoveAt(limit);
        var next = more ? EncodeCursor(beneficiaryId, rows[^1].OccurredAtUtc, rows[^1].Id) : null;
        return new InteractionPageV1(1, rows.Select(Project).ToArray(), next, more);
    }

    public async Task<InteractionV1> DirectAsync(int beneficiaryId, int interactionId,
        CancellationToken token)
    {
        await DemandDossierAsync(beneficiaryId, token);
        var row = await database.Visible<Interaction>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == interactionId && x.ClientId == beneficiaryId, token);
        return row is null ? throw new BeneficiaryWireException("NOT_FOUND") : Project(row);
    }

    public async Task<RecordInteractionCommand?> RecoverLinkedCommandAsync(int beneficiaryId,
        DateTime occurredAtUtc, string note, string idempotencyKey, FollowUpActionInput action,
        Guid actorMembershipId, CancellationToken token)
    {
        // Visibility comes first. The receipt anchors its original case, not a replacement case.
        await DemandDossierAsync(beneficiaryId, token);
        var receipt = await database.Visible<IdempotencyReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorMembershipId == actorMembershipId
                && x.Operation == RecordOperation && x.ClientKey == idempotencyKey, token);
        if (receipt is null) return null;
        if (receipt.ClientId != beneficiaryId || receipt.InteractionId is not int id)
            throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        var interaction = await database.Visible<Interaction>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ClientId == beneficiaryId, token)
            ?? throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        // IActionWriteStore retains sole ownership of the canonical receipt comparator.
        return new RecordInteractionCommand(beneficiaryId, interaction.SocialCaseId,
            occurredAtUtc, note, idempotencyKey, action);
    }

    public async Task<RecordInteractionCommand> ResolveLinkedCommandAsync(int beneficiaryId,
        DateTime occurredAtUtc, string note, string idempotencyKey, FollowUpActionInput action,
        DateOnly localDay, CancellationToken token)
    {
        await DemandDossierAsync(beneficiaryId, token);
        var day = localDay.ToDateTime(TimeOnly.MinValue);
        var cases = await database.SocialCases.AsNoTracking().Where(x => x.ClientId == beneficiaryId)
            .Where(CurrentAssignment.ActiveOn(day)).Take(2).ToListAsync(token);
        if (cases.Count == 0) throw new ActionWriteConflictException("CONTEXT_REQUIRED");
        if (cases.Count != 1 || !await database.SocialWorkers.AnyAsync(w => w.Id == cases[0].SocialWorkerId
                && !w.Softdelete && w.OrganisationMembershipId != Guid.Empty, token))
            throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        return new RecordInteractionCommand(beneficiaryId, cases[0].Id,
            occurredAtUtc, note, idempotencyKey, action);
    }

    public async Task<RecordInteractionV1> RecordAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token)
    {
        await locks.LockAsync(beneficiaryId, token);
        await DemandDossierAsync(beneficiaryId, token);
        var saved = await FindSavedAsync(beneficiaryId, occurredAtUtc, note,
            idempotencyKey, actorMembershipId, token);
        if (saved is not null) return saved;
        var cases = await database.SocialCases.AsNoTracking().Where(x => x.ClientId == beneficiaryId)
            .Where(CurrentAssignment.ActiveOn(DateTime.Today)).Take(2).ToListAsync(token);
        if (cases.Count == 0) throw new ActionWriteConflictException("CONTEXT_REQUIRED");
        if (cases.Count != 1) throw new ActionWriteConflictException("CONTEXT_CONFLICT");
        var command = new RecordInteractionCommand(beneficiaryId, cases[0].Id,
            occurredAtUtc, note, idempotencyKey);
        var result = await actionWrites.RecordAsync(command, actorMembershipId, token);
        var interaction = await database.Visible<Interaction>().AsNoTracking()
            .SingleAsync(x => x.Id == result.InteractionId && x.ClientId == beneficiaryId, token);
        return new RecordInteractionV1(1, result.InteractionId, result.Revision, interaction.Created);
    }

    public async Task<RecordInteractionV1?> ReconcileAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token)
    {
        await DemandDossierAsync(beneficiaryId, token);
        return await FindSavedAsync(beneficiaryId, occurredAtUtc, note, idempotencyKey,
            actorMembershipId, token);
    }

    private async Task<RecordInteractionV1?> FindSavedAsync(int beneficiaryId, DateTime occurredAtUtc,
        string note, string idempotencyKey, Guid actorMembershipId, CancellationToken token)
    {
        var receipt = await database.Visible<IdempotencyReceipt>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.ActorMembershipId == actorMembershipId
                && x.Operation == RecordOperation && x.ClientKey == idempotencyKey, token);
        if (receipt is null) return null;
        if (receipt.ClientId != beneficiaryId || receipt.InteractionId is not int id)
            throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        var interaction = await database.Visible<Interaction>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id && x.ClientId == beneficiaryId, token)
            ?? throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        var command = new RecordInteractionCommand(beneficiaryId, interaction.SocialCaseId,
            occurredAtUtc, note, idempotencyKey);
        // The #76 receipt comparator checks all canonical metadata and ordinal note.
        var result = await actionWrites.ReconcileAsync(command, actorMembershipId, token)
            ?? throw new ActionWriteConflictException("IDEMPOTENCY_CONFLICT");
        return new RecordInteractionV1(1, result.InteractionId, result.Revision, interaction.Created);
    }

    private async Task<Client> DemandDossierAsync(int beneficiaryId, CancellationToken token) =>
        await database.Visible<Client>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == beneficiaryId, token)
            ?? throw new BeneficiaryWireException("NOT_FOUND");

    private static InteractionV1 Project(Interaction row) => new(row.Id, row.OccurredAtUtc,
        row.Created, row.AuthorMembershipId, row.Revision, row.Note);

    private static string EncodeCursor(int beneficiaryId, DateTime occurredAtUtc, int interactionId)
    {
        var json = JsonSerializer.Serialize(new Cursor(1, beneficiaryId,
            occurredAtUtc.ToString("O", CultureInfo.InvariantCulture), interactionId));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=')
            .Replace('+', '-').Replace('/', '_');
    }

    private static (DateTime OccurredAtUtc, int InteractionId) DecodeCursor(string cursor, int beneficiaryId)
    {
        if (cursor.Length is < 1 or > 256 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new BeneficiaryWireException("INVALID_CURSOR");
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4)
                throw new FormatException();
            var value = JsonSerializer.Deserialize<Cursor>(bytes) ?? throw new FormatException();
            if (value.Version != 1 || value.BeneficiaryId != beneficiaryId || value.InteractionId <= 0
                || !DateTime.TryParseExact(value.OccurredAtUtc, "O", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var time) || time.Kind != DateTimeKind.Utc
                || time.Ticks % TimeSpan.TicksPerMillisecond != 0
                || EncodeCursor(value.BeneficiaryId, time, value.InteractionId) != cursor)
                throw new FormatException();
            return (time, value.InteractionId);
        }
        catch (Exception error) when (error is FormatException or JsonException or OverflowException)
        {
            throw new BeneficiaryWireException("INVALID_CURSOR");
        }
    }

    private sealed record Cursor(int Version, int BeneficiaryId, string OccurredAtUtc, int InteractionId);
}
