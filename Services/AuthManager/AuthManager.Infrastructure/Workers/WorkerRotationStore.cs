using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Workers;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.IdentityModel.JsonWebTokens;
using Zeka.WorkerCredentials;

namespace AuthManager.Infrastructure.Workers;

public sealed class WorkerRotationStore(AuthDbContext database, WorkerIdentitySource identities,
    IIdempotencyStore idempotency, IAuditWriter audit, ISessionAccessTokenIssuer issuer,
    TimeProvider clock, WorkerRotationOptions options) : IWorkerRotationStore
{
    public const string PairOperation = "m1.workers.pair/v1";
    public const string PrepareOperation = "m1.workers.prepare/v1";
    private const string Prefix = "m1.workers.";
    private const string Schema = "zeka.m1.workers/v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record PairMember(string Profile, Guid SubjectId, Guid MembershipId);
    private sealed record Pair(string Schema, Guid OrganisationId, PairMember[] Workers);
    private sealed record Family(string Profile, Guid SubjectId, Guid MembershipId, Guid FamilyId, long Generation,
        DateTimeOffset CreatedAtUtc, DateTimeOffset IssuedAtUtc, DateTimeOffset AccessExpiresAtUtc, DateTimeOffset ExpiresAtUtc,
        string? PayloadSha256, string? PublicationSha256);
    private sealed record Prepared(string Schema, Guid OperationId, Guid OrganisationId, long Generation, Family[] Workers);

    public async Task<WorkerRotationResult> ExecuteAsync(string phase, WorkerRotationCaller caller,
        WorkerRotationRequest request, IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is not null) throw Error("WORKER_CALLER_TRANSACTION_REJECTED");
        var attempted = false;
        return await database.Database.CreateExecutionStrategy().ExecuteAsync(async ct =>
        {
            if (attempted) throw Error("WORKER_ATTEMPT_REPLAY_REFUSED", true);
            attempted = true;
            IDbContextTransaction? transaction = null;
            var commitStarted = false;
            try
            {
                transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await TenantContextInitializer.InitializeAsync(database, transaction, request.OrganisationId, ct);
                await identities.ValidateOwnerAsync(caller, request.OrganisationId, ct);
                var result = phase == "prepare"
                    ? await PrepareAsync(caller, request, ct)
                    : await RevokeOthersAsync(caller, request, deliveries, ct);
                await database.SaveChangesAsync(ct);
                commitStarted = true;
                await transaction.CommitAsync(ct);
                return result;
            }
            catch (Exception error)
            {
                var uncertain = commitStarted;
                if (transaction is not null)
                {
                    using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await transaction.RollbackAsync(rollback.Token); } catch { uncertain = true; }
                }
                database.ChangeTracker.Clear();
                if (uncertain) throw Error("WORKER_COMMIT_OR_ROLLBACK_REQUIRES_INSPECTION", true);
                if (error is WorkerRotationException or OperationCanceledException) throw;
                throw Error("WORKER_TRANSACTION_REJECTED");
            }
            finally
            {
                if (transaction is not null)
                {
                    try { await transaction.DisposeAsync(); }
                    catch { throw Error("WORKER_TRANSACTION_DISPOSAL_REQUIRES_INSPECTION", true); }
                }
            }
        }, cancellationToken);
    }

    private async Task<WorkerRotationResult> PrepareAsync(WorkerRotationCaller caller, WorkerRotationRequest request, CancellationToken ct)
    {
        var prior = await ReadAsync<Prepared>(caller.SubjectId, PrepareOperation, request.OperationId.ToString("D"), ct);
        if (prior is not null)
        {
            ValidatePrepared(prior, request);
            return Result(prior, "ISSUED_REPLAY_NO_PRIVATE_VALUES");
        }
        var all = await database.IdempotencyRecords.AsNoTracking().Where(x => x.UserId == caller.SubjectId
            && x.Operation == PrepareOperation && x.ResourceId == request.OrganisationId && x.Status == IdempotencyStatus.Completed)
            .Select(x => x.SafeResponse).ToListAsync(ct);
        var previous = all.Select(Parse<Prepared>).ToArray();
        foreach (var priorOperation in previous)
            ValidatePrepared(priorOperation, new(priorOperation.OperationId, request.OrganisationId));
        var pair = await ReadAsync<Pair>(caller.SubjectId, PairOperation, request.OrganisationId.ToString("D"), ct);
        if (pair is null)
        {
            if (await database.OrganisationMemberships.AnyAsync(x => x.OrganisationId == request.OrganisationId
                && x.PermissionSetId == AuthManager.Core.Organisations.PermissionSet.StaffProjectionWorkerId, ct))
                throw Error("WORKER_PAIR_REGISTRY_MISSING_INSPECTION_REQUIRED", true);
            await BeginAsync(caller.SubjectId, PairOperation, request.OrganisationId.ToString("D"),
                JsonSerializer.Serialize(new { schema = Schema, request.OrganisationId }, Json), ct);
            var members = new List<PairMember>();
            foreach (var profile in WorkerProfiles.All)
            {
                var created = await identities.CreateAsync(profile, request.OrganisationId, ct);
                members.Add(new(profile, created.SubjectId, created.MembershipId));
            }
            pair = new(Schema, request.OrganisationId, members.ToArray());
            await CompleteAsync(caller.SubjectId, PairOperation, request.OrganisationId.ToString("D"), request.OrganisationId, pair, ct);
        }
        ValidatePair(pair, request.OrganisationId);
        await BeginAsync(caller.SubjectId, PrepareOperation, request.OperationId.ToString("D"),
            JsonSerializer.Serialize(new { schema = Schema, request.OperationId, request.OrganisationId }, Json), ct);
        var generation = await NextGenerationAsync(previous.Select(x => x.Generation).DefaultIfEmpty(0).Max(), ct);
        var saved = new List<Family>();
        var payload = new List<WorkerPreparedIdentity>();
        foreach (var member in pair.Workers)
        {
            var user = await identities.ValidateWorkerAsync(member.SubjectId, member.MembershipId, request.OrganisationId, ct);
            var sampled = clock.GetUtcNow();
            var now = new DateTimeOffset(sampled.UtcTicks - sampled.UtcTicks % 10, TimeSpan.Zero);
            var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = user.Id, SecurityStamp = user.SecurityStamp!,
                CreatedAtUtc = now, IdleExpiresAtUtc = now.AddMinutes(60), AbsoluteExpiresAtUtc = now.AddMinutes(60) };
            database.BrowserSessionFamilies.Add(family);
            var value = await issuer.IssueSessionAsync(user.Id, family.Id, TimeSpan.FromMinutes(60), ct);
            var jwt = new JsonWebToken(value);
            var issued = new DateTimeOffset(jwt.IssuedAt, TimeSpan.Zero);
            var expires = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
            if (jwt.Subject != user.Id.ToString("D") || !jwt.TryGetClaim("sid", out var sid) || sid.Value != family.Id.ToString("D")
                || expires <= clock.GetUtcNow() || expires - issued != TimeSpan.FromMinutes(60))
                throw Error("WORKER_ISSUED_SESSION_REJECTED");
            var package = WorkerCredentialEncoding.Create(request.OperationId, generation, user.Id, family.Id, member.Profile,
                [request.OrganisationId], issued, expires, now, family.AbsoluteExpiresAtUtc, value);
            var publicationHash = Convert.ToHexString(SHA256.HashData(WorkerCredentialEncoding.Serialize(package))).ToLowerInvariant();
            var metadata = new Family(member.Profile, user.Id, member.MembershipId, family.Id, generation, now, issued, expires,
                family.AbsoluteExpiresAtUtc, package.PayloadSha256, publicationHash);
            saved.Add(metadata);
            payload.Add(ToPrivate(metadata, value));
        }
        var prepared = new Prepared(Schema, request.OperationId, request.OrganisationId, generation, saved.ToArray());
        await CompleteAsync(caller.SubjectId, PrepareOperation, request.OperationId.ToString("D"), request.OrganisationId, prepared, ct);
        await AuditAsync(caller.SubjectId, request, "prepared", generation, ct);
        return new() { OperationId = request.OperationId, OrganisationId = request.OrganisationId, State = "ISSUED", Workers = payload };
    }

    private async Task<WorkerRotationResult> RevokeOthersAsync(WorkerRotationCaller caller, WorkerRotationRequest request,
        IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken ct)
    {
        if (!WorkerRotationService.ValidDeliveries(deliveries, options.RegistryId))
            throw Error("WORKER_BOTH_VERIFIED_DELIVERIES_REQUIRED");
        var prepared = await ReadAsync<Prepared>(caller.SubjectId, PrepareOperation, request.OperationId.ToString("D"), ct)
            ?? throw Error("WORKER_OPERATION_NOT_FOUND");
        ValidatePrepared(prepared, request);
        var pair = await ReadAsync<Pair>(caller.SubjectId, PairOperation, request.OrganisationId.ToString("D"), ct)
            ?? throw Error("WORKER_PAIR_REGISTRY_MISSING_INSPECTION_REQUIRED", true);
        ValidatePair(pair, request.OrganisationId);
        foreach (var worker in prepared.Workers)
        {
            var member = pair.Workers.Single(x => x.Profile == worker.Profile);
            var delivery = deliveries!.Single(x => x.Profile == worker.Profile);
            if (member.SubjectId == caller.SubjectId || member.SubjectId != worker.SubjectId || member.MembershipId != worker.MembershipId
                || delivery.OperationId != request.OperationId || delivery.Generation != worker.Generation
                || delivery.SubjectId != worker.SubjectId || delivery.SessionId != worker.FamilyId
                || string.IsNullOrWhiteSpace(worker.PayloadSha256) || delivery.PayloadSha256 != worker.PayloadSha256
                || string.IsNullOrWhiteSpace(worker.PublicationSha256) || delivery.PublicationSha256 != worker.PublicationSha256)
                throw Error("WORKER_DELIVERY_REVISION_MISMATCH");
        }
        var metadata = await database.IdempotencyRecords.AsNoTracking().Where(x => x.UserId == caller.SubjectId
            && x.Operation == PrepareOperation && x.ResourceId == request.OrganisationId && x.Status == IdempotencyStatus.Completed)
            .Select(x => x.SafeResponse).ToListAsync(ct);
        if (metadata.Select(Parse<Prepared>).Any(x => x.Generation > prepared.Generation))
            throw Error("WORKER_STALE_DELIVERY_GENERATION_REFUSED");
        // Validate BOTH current identities/families and exact deliveries before touching any other family.
        await ValidateNewFamiliesAsync(prepared, ct);
        var subjects = pair.Workers.Select(x => x.SubjectId).ToArray();
        var retained = prepared.Workers.Select(x => x.FamilyId).ToArray();
        var others = await database.BrowserSessionFamilies.Where(x => subjects.Contains(x.UserId)
            && !retained.Contains(x.Id) && x.RevokedAtUtc == null).ToListAsync(ct);
        var now = clock.GetUtcNow();
        foreach (var family in others.Where(x => x.IdleExpiresAtUtc > now && x.AbsoluteExpiresAtUtc > now))
            family.RevokedAtUtc = now;
        await RecordPhaseAsync(caller.SubjectId, request, "revoke-others", prepared.Generation, deliveries, ct);
        return Result(prepared, "REVOKED_OTHERS");
    }
    private async Task<long> NextGenerationAsync(long previous, CancellationToken ct)
    {
        if (!database.Database.IsNpgsql()) return checked(previous + 1); // Controlled SQLite tests only.
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "select pg_catalog.txid_current()::bigint";
        var generation = Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        if (generation <= previous || generation <= 0) throw Error("WORKER_MONOTONIC_GENERATION_REJECTED");
        return generation;
    }

    private async Task ValidateNewFamiliesAsync(Prepared prepared, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var worker in prepared.Workers)
        {
            var user = await identities.ValidateWorkerAsync(worker.SubjectId, worker.MembershipId, prepared.OrganisationId, ct);
            var family = await database.BrowserSessionFamilies.SingleOrDefaultAsync(x => x.Id == worker.FamilyId, ct);
            if (family is null || family.UserId != worker.SubjectId || family.SecurityStamp != user.SecurityStamp
                || family.RevokedAtUtc is not null || family.IdleExpiresAtUtc <= now || family.AbsoluteExpiresAtUtc <= now
                || family.CreatedAtUtc != worker.CreatedAtUtc || family.IdleExpiresAtUtc != worker.ExpiresAtUtc
                || family.AbsoluteExpiresAtUtc != worker.ExpiresAtUtc || worker.ExpiresAtUtc - worker.CreatedAtUtc != TimeSpan.FromMinutes(60))
                throw Error("WORKER_NEW_FAMILY_UNAVAILABLE_RECOVERY_REQUIRED");
        }
    }
    private async Task RecordPhaseAsync(Guid actor, WorkerRotationRequest request, string phase, long generation,
        IReadOnlyList<WorkerDeliveryReceipt>? deliveries, CancellationToken ct)
    {
        var operation = Prefix + phase + "/v1";
        var canonical = JsonSerializer.Serialize(new { schema = Schema, request.OperationId, request.OrganisationId, generation,
            deliveries = deliveries?.OrderBy(x => x.Profile, StringComparer.Ordinal).ToArray() }, Json);
        var decision = await idempotency.TryBeginAsync(actor, operation, request.OperationId.ToString("D"), canonical,
            options.ProcessingTimeout, options.Retention, ct);
        if (decision.Kind == IdempotencyDecisionKind.Replay) return;
        if (decision.Kind != IdempotencyDecisionKind.Started) throw Error("WORKER_PHASE_IDEMPOTENCY_CONFLICT");
        await idempotency.CompleteAsync(actor, operation, request.OperationId.ToString("D"), request.OrganisationId, canonical, ct);
        await AuditAsync(actor, request, phase, generation, ct);
    }
    private async Task AuditAsync(Guid actor, WorkerRotationRequest request, string phase, long generation, CancellationToken ct) =>
        await audit.WriteAsync(new(actor, request.OrganisationId, "m1.workers." + phase, "M1WorkerPair",
            request.OrganisationId.ToString("D"), phase, request.OperationId.ToString("D"), new Dictionary<string, string>
            { ["generation"] = generation.ToString(System.Globalization.CultureInfo.InvariantCulture), ["profiles"] = string.Join(',', WorkerProfiles.All),
              ["activation"] = "operator-provisioned machine identities" }), ct);
    private async Task BeginAsync(Guid actor, string operation, string key, string canonical, CancellationToken ct)
    {
        var decision = await idempotency.TryBeginAsync(actor, operation, key, canonical, options.ProcessingTimeout, options.Retention, ct);
        if (decision.Kind != IdempotencyDecisionKind.Started) throw Error("WORKER_IDEMPOTENCY_CONFLICT");
    }
    private Task CompleteAsync<T>(Guid actor, string operation, string key, Guid org, T value, CancellationToken ct) =>
        idempotency.CompleteAsync(actor, operation, key, org, JsonSerializer.Serialize(value, Json), ct);
    private async Task<T?> ReadAsync<T>(Guid actor, string operation, string key, CancellationToken ct) where T : class
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var row = await database.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == actor
            && x.Operation == operation && x.KeyHash == hash, ct);
        if (row is null) return null;
        if (row.Status != IdempotencyStatus.Completed || row.SafeResponse is null) throw Error("WORKER_LEDGER_INSPECTION_REQUIRED", true);
        return Parse<T>(row.SafeResponse);
    }
    private static T Parse<T>(string? json) => JsonSerializer.Deserialize<T>(json ?? "", Json) ?? throw Error("WORKER_LEDGER_REJECTED");
    private static void ValidatePair(Pair pair, Guid org)
    {
        if (pair.Schema != Schema || pair.OrganisationId != org || pair.Workers.Length != 2
            || pair.Workers.Select(x => x.SubjectId).Distinct().Count() != 2 || pair.Workers.Select(x => x.MembershipId).Distinct().Count() != 2
            || WorkerProfiles.All.Any(profile => pair.Workers.Count(x => x.Profile == profile) != 1)
            || pair.Workers.Any(x => x.SubjectId == Guid.Empty || x.MembershipId == Guid.Empty)) throw Error("WORKER_PAIR_LEDGER_REJECTED");
    }
    private static void ValidatePrepared(Prepared value, WorkerRotationRequest request)
    {
        if (value.Schema != Schema || value.OperationId != request.OperationId || value.OrganisationId != request.OrganisationId
            || value.Generation <= 0 || value.Workers.Length != 2 || value.Workers.Any(x => x.Generation != value.Generation || x.FamilyId == Guid.Empty)
            || value.Workers.Select(x => x.FamilyId).Distinct().Count() != 2
            || WorkerProfiles.All.Any(profile => value.Workers.Count(x => x.Profile == profile) != 1)) throw Error("WORKER_PREPARED_LEDGER_REJECTED");
    }
    private static WorkerPreparedIdentity ToPrivate(Family value, string? privateValue = null) => new()
    { Profile = value.Profile, SubjectId = value.SubjectId, MembershipId = value.MembershipId, SessionId = value.FamilyId,
      Generation = value.Generation, SessionCreatedAtUtc = value.CreatedAtUtc, TokenIssuedAtUtc = value.IssuedAtUtc,
      AccessExpiresAtUtc = value.AccessExpiresAtUtc, SessionExpiresAtUtc = value.ExpiresAtUtc,
      PayloadSha256 = value.PayloadSha256, PublicationSha256 = value.PublicationSha256, Token = privateValue };
    private static WorkerRotationResult Result(Prepared value, string state) => new()
    { OperationId = value.OperationId, OrganisationId = value.OrganisationId, State = state, Workers = value.Workers.Select(x => ToPrivate(x)).ToArray() };
    private static WorkerRotationException Error(string code, bool inspection = false) => new(code, inspection);
}
