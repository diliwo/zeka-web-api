using System.Security.Claims;
using System.Security.Cryptography;
using AuthManager.API.Endpoints;
using AuthManager.Application.Authentication;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Workers;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Authentication;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Workers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Zeka.WorkerCredentials;

namespace Infrastructure.IntegrationTests;

// Actual SQLite Identity/persistence and real ephemeral RSA issuer. PostgreSQL row locks,
// production grants, actual TLS, protected volumes and live worker recovery remain separate gates.
public sealed class WorkerRotationPersistenceTests
{
    [Fact]
    public async Task Issue_creates_distinct_least_privileged_users_real_60_minute_families_and_exact_envelope_hashes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        Assert.Equal("ISSUED", first.State); Assert.Equal(2, first.Workers.Count);
        Assert.Equal(2, first.Workers.Select(x => x.SubjectId).Distinct().Count());
        foreach (var worker in first.Workers)
        {
            var user = await fixture.Database.Users.SingleAsync(x => x.Id == worker.SubjectId);
            var membership = await fixture.Database.OrganisationMemberships.SingleAsync(x => x.Id == worker.MembershipId);
            var family = await fixture.Database.BrowserSessionFamilies.SingleAsync(x => x.Id == worker.SessionId);
            Assert.Null(user.PasswordHash); Assert.True(user.EmailConfirmed && user.SecurityStamp == family.SecurityStamp);
            Assert.Equal(PermissionSet.StaffProjectionWorkerId, membership.PermissionSetId);
            Assert.Equal(fixture.Options.OrganisationId, membership.OrganisationId);
            Assert.Single(TenantPermissions.Resolve(PermissionSet.StaffProjectionWorkerId, PermissionSet.StaffProjectionWorkerCode)!);
            Assert.Equal(TimeSpan.FromMinutes(60), worker.AccessExpiresAtUtc - worker.TokenIssuedAtUtc);
            Assert.Equal(TimeSpan.FromMinutes(60), family.IdleExpiresAtUtc - family.CreatedAtUtc);
            Assert.Equal(family.IdleExpiresAtUtc, family.AbsoluteExpiresAtUtc);
            Assert.Equal(family.CreatedAtUtc, worker.SessionCreatedAtUtc); Assert.Equal(0, family.CreatedAtUtc.UtcTicks % 10);
            var package = WorkerCredentialEncoding.Create(first.OperationId, worker.Generation, worker.SubjectId, worker.SessionId,
                worker.Profile, [first.OrganisationId], worker.TokenIssuedAtUtc, worker.AccessExpiresAtUtc,
                worker.SessionCreatedAtUtc, worker.SessionExpiresAtUtc, worker.Token!);
            Assert.True(worker.PayloadSha256 == package.PayloadSha256);
            Assert.True(worker.PublicationSha256 == Convert.ToHexString(SHA256.HashData(WorkerCredentialEncoding.Serialize(package))).ToLowerInvariant());
            var leaked = (await fixture.Database.IdempotencyRecords.ToListAsync()).Any(x => x.SafeResponse?.Contains(worker.Token!, StringComparison.Ordinal) == true)
                || (await fixture.Database.AuditEntries.ToListAsync()).Any(x => x.Metadata.Contains(worker.Token!, StringComparison.Ordinal));
            Assert.False(leaked);
        }
        Assert.Empty(await fixture.Database.BrowserRefreshCredentials.ToListAsync());
    }

    [Fact]
    public async Task Same_operation_replays_only_metadata_never_remints_and_fresh_owner_operation_issues_new_pair()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); var replay = await fixture.ExecuteAsync("prepare");
        Assert.Equal("ISSUED_REPLAY_NO_PRIVATE_VALUES", replay.State);
        Assert.All(replay.Workers, worker => Assert.Null(worker.Token));
        Assert.Equal(first.Workers.Select(x => x.SessionId), replay.Workers.Select(x => x.SessionId));
        Assert.Equal(2, fixture.Issues);
        var next = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        Assert.Equal(4, fixture.Issues);
        Assert.Equal(first.Workers.Select(x => x.SubjectId), next.Workers.Select(x => x.SubjectId));
        Assert.True(next.Workers.All(x => x.Generation > first.Workers[0].Generation));
        Assert.Empty(first.Workers.Select(x => x.SessionId).Intersect(next.Workers.Select(x => x.SessionId)));
    }

    [Fact]
    public async Task Expired_unfinished_pair_does_not_block_fresh_owner_run_or_require_a_recovery_phase()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); fixture.Clock.Now = fixture.Clock.Now.AddMinutes(61);
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", deliveries: fixture.Deliveries(first)));
        var next = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        Assert.Equal("ISSUED", next.State);
        Assert.True(next.Workers.All(x => x.Generation > first.Workers[0].Generation && x.SessionExpiresAtUtc > fixture.Clock.Now));
        Assert.Equal("REVOKED_OTHERS", (await fixture.ExecuteAsync("revoke-others", next.OperationId, fixture.Deliveries(next))).State);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("subject")]
    [InlineData("session")]
    [InlineData("generation")]
    [InlineData("operation")]
    [InlineData("payload")]
    [InlineData("publication")]
    public async Task One_missing_or_mismatched_delivery_never_revokes_any_family_and_next_run_can_converge(string defect)
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); var second = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        var deliveries = fixture.Deliveries(second);
        if (defect == "partial") deliveries = [deliveries[0]];
        if (defect == "subject") deliveries[1] = deliveries[1] with { SubjectId = first.Workers[0].SubjectId };
        if (defect == "session") deliveries[1] = deliveries[1] with { SessionId = first.Workers[1].SessionId };
        if (defect == "generation") deliveries[1] = deliveries[1] with { Generation = deliveries[1].Generation + 1 };
        if (defect == "operation") deliveries[1] = deliveries[1] with { OperationId = first.OperationId };
        if (defect == "payload") deliveries[1] = deliveries[1] with { PayloadSha256 = Digest() };
        if (defect == "publication") deliveries[1] = deliveries[1] with { PublicationSha256 = Digest() };
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", second.OperationId, deliveries));
        Assert.All(await fixture.Database.BrowserSessionFamilies.ToListAsync(), family => Assert.Null(family.RevokedAtUtc));
        var third = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        await fixture.ExecuteAsync("revoke-others", third.OperationId, fixture.Deliveries(third));
        Assert.All(first.Workers.Concat(second.Workers), worker => Assert.NotNull(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
        Assert.All(third.Workers, worker => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
    }

    [Fact]
    public async Task Revoke_retains_only_current_family_for_each_exact_worker_never_owner_other_human_or_unrelated_worker()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        var unrelated = await fixture.AddUnrelatedAsync();
        foreach (var worker in first.Workers) await fixture.AddFamilyAsync(worker.SubjectId);
        var current = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        await fixture.ExecuteAsync("revoke-others", current.OperationId, fixture.Deliveries(current));
        foreach (var worker in current.Workers)
        {
            var families = await fixture.Database.BrowserSessionFamilies.Where(x => x.UserId == worker.SubjectId).ToListAsync();
            Assert.Single(families.Where(x => x.RevokedAtUtc is null));
            Assert.Null(families.Single(x => x.Id == worker.SessionId).RevokedAtUtc);
            Assert.All(families.Where(x => x.Id != worker.SessionId), family => Assert.NotNull(family.RevokedAtUtc));
        }
        Assert.Null((await fixture.Database.BrowserSessionFamilies.SingleAsync(x => x.Id == fixture.Caller.SessionId)).RevokedAtUtc);
        Assert.All(unrelated, id => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == id).RevokedAtUtc));
        var replay = await fixture.ExecuteAsync("revoke-others", current.OperationId, fixture.Deliveries(current));
        Assert.Equal("REVOKED_OTHERS", replay.State); Assert.All(replay.Workers, worker => Assert.Null(worker.Token));
        Assert.Equal(1, await fixture.Database.AuditEntries.CountAsync(x => x.Action == "m1.workers.revoke-others"));
    }

    [Fact]
    public async Task Old_delivery_receipts_cannot_roll_back_a_later_issued_generation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); await fixture.ExecuteAsync("revoke-others", deliveries: fixture.Deliveries(first));
        var later = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", deliveries: fixture.Deliveries(first)));
        Assert.All(later.Workers, worker => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
    }

    [Fact]
    public async Task Failed_revocation_rolls_back_both_workers_and_explicit_new_owner_run_converges()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); var second = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        fixture.FailRevokeAudit = true;
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", second.OperationId, fixture.Deliveries(second)));
        Assert.All(await fixture.Database.BrowserSessionFamilies.ToListAsync(), family => Assert.Null(family.RevokedAtUtc));
        Assert.Equal(0, await fixture.Database.AuditEntries.CountAsync(x => x.Action == "m1.workers.revoke-others"));
        Assert.Equal(0, await fixture.Database.IdempotencyRecords.CountAsync(x => x.Operation == "m1.workers.revoke-others/v1"));
        fixture.FailRevokeAudit = false;
        var third = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        await fixture.ExecuteAsync("revoke-others", third.OperationId, fixture.Deliveries(third));
        Assert.All(first.Workers.Concat(second.Workers), worker => Assert.NotNull(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
    }

    [Theory]
    [InlineData("replacement-revoked")]
    [InlineData("replacement-stamp")]
    [InlineData("worker-membership")]
    [InlineData("owner-membership")]
    [InlineData("organisation-closing")]
    public async Task Revalidation_failure_after_delivery_preserves_all_previous_worker_families(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare"); var next = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        var worker = next.Workers[1];
        if (change == "replacement-revoked") (await fixture.Database.BrowserSessionFamilies.SingleAsync(x => x.Id == worker.SessionId)).RevokedAtUtc = fixture.Clock.Now;
        if (change == "replacement-stamp") (await fixture.Database.Users.SingleAsync(x => x.Id == worker.SubjectId)).SecurityStamp = Guid.NewGuid().ToString("N");
        if (change == "worker-membership") (await fixture.Database.OrganisationMemberships.SingleAsync(x => x.Id == worker.MembershipId)).Suspend(fixture.Clock.Now);
        if (change == "owner-membership") (await fixture.Database.OrganisationMemberships.SingleAsync(x => x.UserId == fixture.Caller.SubjectId)).Suspend(fixture.Clock.Now);
        if (change == "organisation-closing") (await fixture.Database.Organisations.SingleAsync()).BeginClosure(fixture.Clock.Now);
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", next.OperationId, fixture.Deliveries(next)));
        Assert.All(first.Workers, previous => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == previous.SessionId).RevokedAtUtc));
        Assert.Equal(0, await fixture.Database.AuditEntries.CountAsync(x => x.Action == "m1.workers.revoke-others"));
    }

    [Theory]
    [InlineData("owner-session")]
    [InlineData("owner-membership")]
    [InlineData("closed-organisation")]
    public async Task Current_owner_or_lifecycle_denial_prevents_worker_creation(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (change == "owner-session") (await fixture.Database.BrowserSessionFamilies.SingleAsync()).RevokedAtUtc = fixture.Clock.Now;
        if (change == "owner-membership") (await fixture.Database.OrganisationMemberships.SingleAsync()).Suspend(fixture.Clock.Now);
        if (change == "closed-organisation") (await fixture.Database.Organisations.SingleAsync()).Close(fixture.Clock.Now);
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare"));
        Assert.Equal(1, await fixture.Database.Users.CountAsync()); Assert.Empty(await fixture.Database.IdempotencyRecords.ToListAsync());
    }

    [Fact]
    public async Task Issuer_failure_on_second_worker_rolls_back_identities_families_audit_and_idempotency()
    {
        await using var fixture = await Fixture.CreateAsync(); fixture.FailSecondIssue = true;
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare"));
        Assert.Equal(1, await fixture.Database.Users.CountAsync()); Assert.Equal(1, await fixture.Database.BrowserSessionFamilies.CountAsync());
        Assert.Empty(await fixture.Database.IdempotencyRecords.ToListAsync()); Assert.Empty(await fixture.Database.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Missing_pair_registry_does_not_recreate_subjects_and_changed_worker_catalogue_prevents_retirement()
    {
        await using var fixture = await Fixture.CreateAsync(); var first = await fixture.ExecuteAsync("prepare");
        var role = await fixture.Database.PermissionSets.SingleAsync(x => x.Id == PermissionSet.StaffProjectionWorkerId);
        fixture.Database.Entry(role).Property(x => x.Code).CurrentValue = "UnrecognizedWorker"; await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("revoke-others", deliveries: fixture.Deliveries(first)));
        Assert.All(await fixture.Database.BrowserSessionFamilies.ToListAsync(), family => Assert.Null(family.RevokedAtUtc));
        fixture.Database.IdempotencyRecords.Remove(await fixture.Database.IdempotencyRecords.SingleAsync(x => x.Operation == WorkerRotationStore.PairOperation));
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare", Guid.NewGuid())); Assert.Equal(3, await fixture.Database.Users.CountAsync());
    }

    [Fact]
    public void Endpoint_admission_rejects_missing_duplicate_or_unauthenticated_session_claims()
    {
        var subject = Guid.NewGuid().ToString("D"); var sid = Guid.NewGuid().ToString("D");
        Assert.False(WorkerRotationEndpoints.TryCaller(new ClaimsPrincipal(new ClaimsIdentity(new Claim[] { new("sub", subject), new("sid", sid) })), out _));
        Assert.False(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject)], "test")), out _));
        Assert.False(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject), new("sid", sid), new("sid", sid)], "test")), out _));
        Assert.True(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject), new("sid", sid)], "test")), out _));
    }

    private static string Digest() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new DateTimeOffset(2026, 11, 2, 15, 30, 0, 123, TimeSpan.Zero).AddTicks(7); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class MemoryKey : ISigningKeyProvider, IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        public Task<RSA> OpenPrivateKeyAsync(string reference, CancellationToken ct) { var copy = RSA.Create(); copy.ImportParameters(rsa.ExportParameters(true)); return Task.FromResult(copy); }
        public Task<RSA> OpenPublicKeyAsync(string reference, CancellationToken ct) { var copy = RSA.Create(); copy.ImportParameters(rsa.ExportParameters(false)); return Task.FromResult(copy); }
        public void Dispose() => rsa.Dispose();
    }
    private sealed class FailingAudit(IAuditWriter inner) : IAuditWriter
    {
        public async Task<Guid> WriteAsync(AuditEntryRequest request, CancellationToken cancellationToken = default)
        { var id = await inner.WriteAsync(request, cancellationToken); if (request.Action == "m1.workers.revoke-others") throw new InvalidOperationException("Controlled retirement failure."); return id; }
    }
    private sealed class Fixture(IdentityTestContext context, AsyncServiceScope scope, Clock clock, MemoryKey key,
        AccessTokenIssuer issuer, WorkerRotationOptions options, WorkerRotationCaller caller) : IAsyncDisposable, ISessionAccessTokenIssuer
    {
        public AuthDbContext Database => scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        public Clock Clock => clock; public WorkerRotationOptions Options => options; public WorkerRotationCaller Caller => caller;
        public Guid Operation = Guid.NewGuid(); public bool FailSecondIssue; public bool FailRevokeAudit; public int Issues;
        public static async Task<Fixture> CreateAsync()
        {
            var clock = new Clock(); var context = await IdentityTestContext.CreateAsync(configureServices: services => services.AddSingleton<TimeProvider>(clock)); var scope = context.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var owner = User.Create("operator@example.invalid", "operator", "Synthetic", "Owner", clock.Now); owner.EmailConfirmed = true; owner.ActivateConfirmedIdentity(clock.Now);
            Assert.True((await users.CreateAsync(owner)).Succeeded);
            var org = Organisation.Create(Guid.NewGuid(), "Controlled M1", owner.Id, clock.Now); Assert.True(org.Activate(clock.Now)); db.Organisations.Add(org);
            db.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), org.Id, owner.Id, clock.Now));
            // This controlled admitted owner family remains valid while worker expiry is exercised; no browser policy is changed.
            var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = owner.Id, SecurityStamp = owner.SecurityStamp!, CreatedAtUtc = clock.Now,
                IdleExpiresAtUtc = clock.Now.AddHours(4), AbsoluteExpiresAtUtc = clock.Now.AddHours(4) }; db.BrowserSessionFamilies.Add(family); await db.SaveChangesAsync();
            var key = new MemoryKey(); var issuer = new AccessTokenIssuer(key, new IssuerOptions { ActiveKeyId = "controlled", ActiveSigningKeyReference = "in-memory", AccessTokenLifetime = TimeSpan.FromMinutes(30) },
                new Zeka.Authentication.AuthenticationOptions { Issuer = "https://auth.example.invalid", Audience = "controlled" }, clock); issuer.ConfirmPublication(await issuer.PrepareAsync());
            return new(context, scope, clock, key, issuer, new WorkerRotationOptions { Enabled = true, LocalStack = true, OwnerSubjectId = owner.Id,
                OrganisationId = org.Id, RegistryId = Guid.NewGuid(), ProcessingTimeout = TimeSpan.FromSeconds(120), Retention = TimeSpan.FromDays(1) }, new(owner.Id, family.Id));
        }
        public Task<WorkerRotationResult> ExecuteAsync(string phase, Guid? operation = null, IReadOnlyList<WorkerDeliveryReceipt>? deliveries = null)
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditWriter>(); if (FailRevokeAudit) audit = new FailingAudit(audit);
            var store = new WorkerRotationStore(Database, new WorkerIdentitySource(Database, scope.ServiceProvider.GetRequiredService<UserManager<User>>(), clock),
                scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(), audit, this, clock, options);
            return new WorkerRotationService(store, options).ExecuteAsync(phase, caller, new(operation ?? Operation, options.OrganisationId), deliveries);
        }
        public WorkerDeliveryReceipt[] Deliveries(WorkerRotationResult result) => result.Workers.Select(worker => new WorkerDeliveryReceipt(worker.Profile,
            result.OperationId, worker.Generation, worker.SubjectId, worker.SessionId, worker.PayloadSha256!, worker.PublicationSha256!,
            WorkerRotationService.ExpectedVolume(worker.Profile, options.RegistryId), "/run/zeka-worker-credential/current.json", 1654, 1654, "0500", "0400",
            worker.Profile == WorkerProfiles.AdminAreaRetry ? "adminarea" : "client", true)).ToArray();
        public async Task<Guid> AddFamilyAsync(Guid userId)
        {
            var user = await Database.Users.SingleAsync(x => x.Id == userId); var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = userId,
                SecurityStamp = user.SecurityStamp!, CreatedAtUtc = clock.Now, IdleExpiresAtUtc = clock.Now.AddHours(1), AbsoluteExpiresAtUtc = clock.Now.AddHours(1) };
            Database.BrowserSessionFamilies.Add(family); await Database.SaveChangesAsync(); return family.Id;
        }
        public async Task<Guid[]> AddUnrelatedAsync()
        {
            var result = new List<Guid>(); var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            foreach (var worker in new[] { false, true })
            {
                var name = "unrelated-" + Guid.NewGuid().ToString("N"); var user = User.Create(name+"@example.invalid", name, "Synthetic", "Unrelated", clock.Now);
                user.EmailConfirmed = true; user.ActivateConfirmedIdentity(clock.Now); Assert.True((await users.CreateAsync(user)).Succeeded);
                Database.OrganisationMemberships.Add(OrganisationMembership.Create(Guid.NewGuid(), options.OrganisationId, user.Id,
                    worker ? PermissionSet.StaffProjectionWorkerId : PermissionSet.EditorId, clock.Now)); await Database.SaveChangesAsync(); result.Add(await AddFamilyAsync(user.Id));
            }
            return result.ToArray();
        }
        public Task<string> IssueSessionAsync(Guid subject, Guid session, TimeSpan lifetime, CancellationToken ct)
        { Issues++; if (FailSecondIssue && Issues == 2) throw new InvalidOperationException("Controlled second issue refusal."); return issuer.IssueSessionAsync(subject, session, lifetime, ct); }
        public async ValueTask DisposeAsync() { key.Dispose(); await scope.DisposeAsync(); await context.DisposeAsync(); }
    }
}
