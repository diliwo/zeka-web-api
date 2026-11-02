using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
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

namespace Infrastructure.IntegrationTests;

// Real SQLite Identity/persistence and the real in-memory RSA issuer. PostgreSQL row locks,
// production grants, actual TLS admission and live worker adoption are not proved here.
public sealed class WorkerRotationPersistenceTests
{
    [Fact]
    public async Task Prepare_creates_distinct_least_privileged_users_and_real_60_minute_families_without_refresh_or_password()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        Assert.Equal("PREPARED", first.State);
        Assert.Equal(2, first.Workers.Count);
        Assert.Equal(2, first.Workers.Select(x => x.SubjectId).Distinct().Count());
        foreach (var worker in first.Workers)
        {
            var user = await fixture.Database.Users.SingleAsync(x => x.Id == worker.SubjectId);
            var membership = await fixture.Database.OrganisationMemberships.SingleAsync(x => x.Id == worker.MembershipId);
            var family = await fixture.Database.BrowserSessionFamilies.SingleAsync(x => x.Id == worker.SessionId);
            Assert.Null(user.PasswordHash);
            Assert.True(user.EmailConfirmed && user.SecurityStamp == family.SecurityStamp);
            Assert.Equal(PermissionSet.StaffProjectionWorkerId, membership.PermissionSetId);
            Assert.Equal(fixture.Options.OrganisationId, membership.OrganisationId);
            Assert.Single(TenantPermissions.Resolve(PermissionSet.StaffProjectionWorkerId, PermissionSet.StaffProjectionWorkerCode)!);
            Assert.Equal(TimeSpan.FromMinutes(60), worker.AccessExpiresAtUtc - worker.TokenIssuedAtUtc);
            Assert.Equal(TimeSpan.FromMinutes(60), family.IdleExpiresAtUtc - family.CreatedAtUtc);
            Assert.Equal(family.IdleExpiresAtUtc, family.AbsoluteExpiresAtUtc);
            Assert.Equal(family.CreatedAtUtc, worker.SessionCreatedAtUtc);
            Assert.Equal(0, family.CreatedAtUtc.UtcTicks % 10);
            Assert.False(string.IsNullOrWhiteSpace(worker.Token));
        }
        Assert.Empty(await fixture.Database.BrowserRefreshCredentials.ToListAsync());
        var rows = await fixture.Database.IdempotencyRecords.ToListAsync();
        foreach (var worker in first.Workers)
        {
            var persistedPrivateValue = rows.Any(x => x.SafeResponse?.Contains(worker.Token!, StringComparison.Ordinal) == true);
            Assert.False(persistedPrivateValue);
            var auditedPrivateValue = (await fixture.Database.AuditEntries.ToListAsync()).Any(x => x.Metadata.Contains(worker.Token!, StringComparison.Ordinal));
            Assert.False(auditedPrivateValue);
        }
    }

    [Fact]
    public async Task Lost_prepare_response_replays_only_metadata_and_does_not_mint_or_create_another_family()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        var replay = await fixture.ExecuteAsync("prepare");
        Assert.Equal("PREPARED_REPLAY_RECOVERY_REQUIRED", replay.State);
        Assert.All(replay.Workers, worker => Assert.Null(worker.Token));
        Assert.Equal(first.Workers.Select(x => x.SessionId), replay.Workers.Select(x => x.SessionId));
        Assert.Equal(3, await fixture.Database.BrowserSessionFamilies.CountAsync());
        Assert.Equal(3, await fixture.Database.Users.CountAsync());
    }

    [Fact]
    public async Task Begin_cutover_forbids_abort_and_new_operation_without_revoking_either_new_or_previous_family()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        await fixture.ExecuteAsync("begin-cutover");
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("abort"));
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare", Guid.NewGuid()));
        Assert.Equal("CUTOVER_STARTED", (await fixture.ExecuteAsync("inspect")).State);
        Assert.All(await fixture.Database.BrowserSessionFamilies.ToListAsync(), family => Assert.Null(family.RevokedAtUtc));
        Assert.All(first.Workers, worker => Assert.NotEqual(Guid.Empty, worker.SessionId));
    }

    [Fact]
    public async Task Abort_before_publication_revokes_only_prepared_families_and_next_generation_is_larger()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        var aborted = await fixture.ExecuteAsync("abort");
        Assert.Equal("ABORTED", aborted.State);
        Assert.All(first.Workers, worker => Assert.NotNull(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
        Assert.Null((await fixture.Database.BrowserSessionFamilies.SingleAsync(x => x.Id == fixture.Caller.SessionId)).RevokedAtUtc);
        var next = await fixture.ExecuteAsync("prepare", Guid.NewGuid());
        Assert.True(next.Workers.All(worker => worker.Generation > first.Workers[0].Generation));
        Assert.Equal(first.Workers.Select(x => x.SubjectId), next.Workers.Select(x => x.SubjectId));
    }

    [Fact]
    public async Task Finalize_requires_both_matching_actual_use_attestations_and_only_then_retires_previous_worker_families()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.ExecuteAsync("prepare");
        await fixture.ExecuteAsync("begin-cutover");
        await fixture.ExecuteAsync("finalize", witnesses: Witnesses(first));
        var secondOperation = Guid.NewGuid();
        var second = await fixture.ExecuteAsync("prepare", secondOperation);
        Assert.All(first.Workers, worker => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("finalize", secondOperation, Witnesses(second)));
        await fixture.ExecuteAsync("begin-cutover", secondOperation);
        var wrong = Witnesses(second); wrong[1] = wrong[1] with { Generation = wrong[1].Generation + 1 };
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("finalize", secondOperation, wrong));
        Assert.All(first.Workers, worker => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
        Assert.Equal("FINALIZED", (await fixture.ExecuteAsync("finalize", secondOperation, Witnesses(second))).State);
        Assert.All(first.Workers, worker => Assert.NotNull(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
        Assert.All(second.Workers, worker => Assert.Null(fixture.Database.BrowserSessionFamilies.Single(x => x.Id == worker.SessionId).RevokedAtUtc));
    }

    [Theory]
    [InlineData("owner-session")]
    [InlineData("owner-membership")]
    [InlineData("closed-organisation")]
    public async Task Current_owner_revocation_membership_or_lifecycle_change_refuses_before_worker_creation(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (change == "owner-session") (await fixture.Database.BrowserSessionFamilies.SingleAsync()).RevokedAtUtc = fixture.Clock.GetUtcNow();
        if (change == "owner-membership") (await fixture.Database.OrganisationMemberships.SingleAsync()).Suspend(fixture.Clock.GetUtcNow());
        if (change == "closed-organisation") (await fixture.Database.Organisations.SingleAsync()).Close(fixture.Clock.GetUtcNow());
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare"));
        Assert.Equal(1, await fixture.Database.Users.CountAsync());
        Assert.Empty(await fixture.Database.IdempotencyRecords.ToListAsync());
    }

    [Fact]
    public async Task Issuer_failure_on_second_worker_rolls_back_identity_sessions_and_all_ledger_and_audit_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.FailSecondIssue = true;
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare"));
        Assert.Equal(1, await fixture.Database.Users.CountAsync());
        Assert.Equal(1, await fixture.Database.OrganisationMemberships.CountAsync());
        Assert.Equal(1, await fixture.Database.BrowserSessionFamilies.CountAsync());
        Assert.Empty(await fixture.Database.IdempotencyRecords.ToListAsync());
        Assert.Empty(await fixture.Database.AuditEntries.ToListAsync());
    }

    [Fact]
    public async Task Missing_pair_registry_never_recreates_existing_worker_subjects()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("prepare");
        await fixture.ExecuteAsync("abort");
        fixture.Database.IdempotencyRecords.Remove(await fixture.Database.IdempotencyRecords.SingleAsync(x => x.Operation == WorkerRotationStore.PairOperation));
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("prepare", Guid.NewGuid()));
        Assert.Equal(3, await fixture.Database.Users.CountAsync());
        Assert.Equal(3, await fixture.Database.OrganisationMemberships.CountAsync());
    }

    [Fact]
    public async Task Modified_worker_catalogue_blocks_cutover_without_retiring_previous_or_prepared_sessions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.ExecuteAsync("prepare");
        var role = await fixture.Database.PermissionSets.SingleAsync(x => x.Id == PermissionSet.StaffProjectionWorkerId);
        fixture.Database.Entry(role).Property(x => x.Code).CurrentValue = "UnrecognizedWorker";
        await fixture.Database.SaveChangesAsync();
        await Assert.ThrowsAsync<WorkerRotationException>(() => fixture.ExecuteAsync("begin-cutover"));
        Assert.All(await fixture.Database.BrowserSessionFamilies.ToListAsync(), family => Assert.Null(family.RevokedAtUtc));
        Assert.Equal("PREPARED", (await fixture.ExecuteAsync("inspect")).State);
    }

    [Fact]
    public void Endpoint_claim_admission_rejects_missing_or_duplicate_sid_and_unauthenticated_claims()
    {
        var subject = Guid.NewGuid().ToString("D"); var sid = Guid.NewGuid().ToString("D");
        Assert.False(WorkerRotationEndpoints.TryCaller(new ClaimsPrincipal(new ClaimsIdentity(new Claim[] { new("sub", subject), new("sid", sid) })), out _));
        Assert.False(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject)], "test")), out _));
        Assert.False(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject), new("sid", sid), new("sid", sid)], "test")), out _));
        Assert.True(WorkerRotationEndpoints.TryCaller(new(new ClaimsIdentity([new("sub", subject), new("sid", sid)], "test")), out _));
    }

    private static WorkerUseWitness[] Witnesses(WorkerRotationResult result) => result.Workers.Select(worker => new WorkerUseWitness(
        worker.Profile, worker.Generation, worker.SessionId, new string('a', 64), "retained/use-1", 10, new string('b', 64), Guid.NewGuid())).ToArray();
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new DateTimeOffset(2026, 11, 2, 14, 0, 0, 123, TimeSpan.Zero).AddTicks(7);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class MemoryKey : ISigningKeyProvider, IDisposable
    {
        private readonly RSA rsa = RSA.Create(2048);
        public Task<RSA> OpenPrivateKeyAsync(string reference, CancellationToken ct)
        { var copy = RSA.Create(); copy.ImportParameters(rsa.ExportParameters(true)); return Task.FromResult(copy); }
        public Task<RSA> OpenPublicKeyAsync(string reference, CancellationToken ct)
        { var copy = RSA.Create(); copy.ImportParameters(rsa.ExportParameters(false)); return Task.FromResult(copy); }
        public void Dispose() => rsa.Dispose();
    }
    private sealed class Fixture(IdentityTestContext context, AsyncServiceScope scope, Clock clock, MemoryKey key,
        AccessTokenIssuer issuer, WorkerRotationOptions options, WorkerRotationCaller caller) : IAsyncDisposable, ISessionAccessTokenIssuer
    {
        public AuthDbContext Database => scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        public Clock Clock => clock;
        public WorkerRotationOptions Options => options;
        public WorkerRotationCaller Caller => caller;
        public Guid Operation = Guid.NewGuid();
        public bool FailSecondIssue;
        private int issues;
        public static async Task<Fixture> CreateAsync()
        {
            var clock = new Clock(); var context = await IdentityTestContext.CreateAsync(clock.Now);
            var scope = context.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var owner = User.Create("operator@example.invalid", "operator", "Synthetic", "Owner", clock.Now);
            owner.EmailConfirmed = true; owner.ActivateConfirmedIdentity(clock.Now);
            Assert.True((await users.CreateAsync(owner)).Succeeded);
            var org = Organisation.Create(Guid.NewGuid(), "Controlled M1", owner.Id, clock.Now); Assert.True(org.Activate(clock.Now));
            db.Organisations.Add(org);
            db.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), org.Id, owner.Id, clock.Now));
            var family = new BrowserSessionFamily { Id = Guid.NewGuid(), UserId = owner.Id, SecurityStamp = owner.SecurityStamp!,
                CreatedAtUtc = clock.Now, IdleExpiresAtUtc = clock.Now.AddHours(1), AbsoluteExpiresAtUtc = clock.Now.AddHours(1) };
            db.BrowserSessionFamilies.Add(family); await db.SaveChangesAsync();
            var key = new MemoryKey();
            var issuer = new AccessTokenIssuer(key, new IssuerOptions { ActiveKeyId = "controlled", ActiveSigningKeyReference = "in-memory", AccessTokenLifetime = TimeSpan.FromMinutes(30) },
                new Zeka.Authentication.AuthenticationOptions { Issuer = "https://auth.example.invalid", Audience = "controlled" }, clock);
            issuer.ConfirmPublication(await issuer.PrepareAsync());
            return new(context, scope, clock, key, issuer, new WorkerRotationOptions { Enabled = true, LocalStack = true,
                OwnerSubjectId = owner.Id, OrganisationId = org.Id, ProcessingTimeout = TimeSpan.FromSeconds(120), Retention = TimeSpan.FromDays(1) }, new(owner.Id, family.Id));
        }
        public Task<WorkerRotationResult> ExecuteAsync(string phase, Guid? operation = null, IReadOnlyList<WorkerUseWitness>? witnesses = null)
        {
            var store = new WorkerRotationStore(Database, new WorkerIdentitySource(Database, scope.ServiceProvider.GetRequiredService<UserManager<User>>(), clock),
                scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(), scope.ServiceProvider.GetRequiredService<IAuditWriter>(), this, clock, options);
            return new WorkerRotationService(store, options).ExecuteAsync(phase, caller, new(operation ?? Operation, options.OrganisationId), witnesses);
        }
        public Task<string> IssueSessionAsync(Guid subject, Guid session, TimeSpan lifetime, CancellationToken ct)
        { issues++; if (FailSecondIssue && issues == 2) throw new InvalidOperationException("Controlled second issue refusal."); return issuer.IssueSessionAsync(subject, session, lifetime, ct); }
        public async ValueTask DisposeAsync() { key.Dispose(); await scope.DisposeAsync(); await context.DisposeAsync(); }
    }
}
