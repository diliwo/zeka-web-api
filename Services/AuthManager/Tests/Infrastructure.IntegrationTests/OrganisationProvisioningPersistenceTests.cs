using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using System.Text.Json;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Provisioning;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

// SQLite proves persistence/rollback and the shared application composition only.
// It does not prove PostgreSQL locks, runtime grants, or actual owner/SID admission.
[Trait("Issue", "ZEK-17")]
public sealed class OrganisationProvisioningPersistenceTests
{
    [Fact]
    public async Task Owner_create_and_persisted_replay_have_one_atomic_identity_organisation_membership_audit_and_outbox()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service();
        var command = new OrganisationProvisioningCommand("Synthetic Organisation", null, "stable-owner-request", null);

        var created = await service.ProvisionAsync(command);
        fixture.Database.ChangeTracker.Clear();
        var replay = await service.ProvisionAsync(command);

        Assert.Equal(OrganisationProvisioningOutcome.Created, created.Outcome);
        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created.SubjectId, replay.SubjectId);
        Assert.Equal(created.OrganisationId, replay.OrganisationId);
        Assert.Equal(created.MembershipId, replay.MembershipId);
        Assert.Equal(1, fixture.Identity.EnsureCalls);
        Assert.Equal(1, await fixture.Database.Users.CountAsync());
        var organisation = await fixture.Database.Organisations.SingleAsync();
        var membership = await fixture.Database.OrganisationMemberships.SingleAsync();
        Assert.Equal(created.SubjectId, organisation.OwnerUserId);
        Assert.Equal(OrganisationStatus.Active, organisation.Status);
        Assert.Null(organisation.TimeZoneId);
        Assert.Equal(0, organisation.TimeZoneConfigVersion);
        Assert.Equal(created.OrganisationId, membership.OrganisationId);
        Assert.Equal(created.SubjectId, membership.UserId);
        Assert.Equal(PermissionSet.OrganisationOwnerId, membership.PermissionSetId);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Contains("Clients.Delete", TenantPermissions.Resolve(membership.PermissionSetId, "Owner")!);
        Assert.Equal(1, await fixture.Database.IdempotencyRecords.CountAsync());
        var audit = await fixture.Database.AuditEntries.SingleAsync();
        Assert.Equal("organisation.provisioned", audit.Action);
        Assert.Equal("Organisation", audit.SubjectType);
        Assert.Contains(OrganisationProvisioningAudit.OperatorNoAgreements, audit.Metadata);
        Assert.Equal(0, await fixture.Database.AuditEntries.CountAsync(value =>
            value.Action == "agreement.accepted" || value.SubjectType == "AgreementAcceptance"));
        var outbox = await fixture.Database.OutboxMessages.SingleAsync();
        Assert.Equal(OrganisationProvisioningEvents.MessageType, outbox.MessageType);
        Assert.Equal(1, outbox.ContractVersion);
        using var payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(3, payload.RootElement.EnumerateObject().Count());
        Assert.Equal(created.OrganisationId, payload.RootElement.GetProperty("OrganisationId").GetGuid());
        Assert.Equal(created.SubjectId, payload.RootElement.GetProperty("OwnerUserId").GetGuid());
        Assert.Equal(created.MembershipId, payload.RootElement.GetProperty("OwnerMembershipId").GetGuid());
        Assert.False(audit.Metadata.Contains(fixture.Identity.Password, StringComparison.Ordinal));
        Assert.False(outbox.Payload.Contains(fixture.Identity.Password, StringComparison.Ordinal));
        Assert.Equal(0, await fixture.Database.BrowserSessionFamilies.CountAsync());
    }

    [Fact]
    public async Task Retained_completed_record_replays_after_retention_expiry_without_resetting_identity_or_creating_rows()
    {
        var clock = new MutableProvisioningClock();
        await using var fixture = await Fixture.CreateAsync(timeProvider: clock);
        var service = fixture.Service();
        var command = new OrganisationProvisioningCommand("Retained replay", null, "retained-owner-request", null);
        var created = await service.ProvisionAsync(command);
        fixture.Database.ChangeTracker.Clear();
        var before = await fixture.Database.Users.AsNoTracking().SingleAsync();
        var completed = await fixture.Database.IdempotencyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(AuthManager.Infrastructure.Persistence.Entities.IdempotencyStatus.Completed, completed.Status);
        clock.Now = completed.RetainUntilUtc.AddDays(1);
        Assert.True(completed.RetainUntilUtc < clock.GetUtcNow());

        var replay = await service.ProvisionAsync(command);

        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created.SubjectId, replay.SubjectId);
        Assert.Equal(created.OrganisationId, replay.OrganisationId);
        Assert.Equal(created.MembershipId, replay.MembershipId);
        Assert.Equal(1, fixture.Identity.EnsureCalls);
        fixture.Database.ChangeTracker.Clear();
        var after = await fixture.Database.Users.AsNoTracking().SingleAsync();
        Assert.True(string.Equals(before.PasswordHash, after.PasswordHash, StringComparison.Ordinal));
        Assert.True(string.Equals(before.SecurityStamp, after.SecurityStamp, StringComparison.Ordinal));
        Assert.Equal(1, await fixture.Database.Users.CountAsync());
        Assert.Equal(1, await fixture.Database.Organisations.CountAsync());
        Assert.Equal(1, await fixture.Database.OrganisationMemberships.CountAsync());
        Assert.Equal(1, await fixture.Database.IdempotencyRecords.CountAsync());
        Assert.Equal(1, await fixture.Database.AuditEntries.CountAsync());
        Assert.Equal(1, await fixture.Database.OutboxMessages.CountAsync());
        var retained = await fixture.Database.IdempotencyRecords.AsNoTracking().SingleAsync();
        Assert.Equal(completed.Id, retained.Id);
        Assert.Equal(completed.RetainUntilUtc, retained.RetainUntilUtc);
        // This proves replay while the completed record is retained. It does not prove
        // uniqueness after deletion, and no independent deletion is an approved M1 operation.
    }

    [Fact]
    public async Task Same_key_with_different_intent_conflicts_without_new_business_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service();
        var command = new OrganisationProvisioningCommand("First", null, "same-key", null);
        var first = await service.ProvisionAsync(command);

        var conflict = await service.ProvisionAsync(command with { DisplayName = "Changed" });

        Assert.Equal(OrganisationProvisioningOutcome.Created, first.Outcome);
        Assert.Equal(OrganisationProvisioningOutcome.Conflict, conflict.Outcome);
        Assert.Equal(1, fixture.Identity.EnsureCalls);
        Assert.Equal(1, await fixture.Database.Organisations.CountAsync());
        Assert.Equal(1, await fixture.Database.OrganisationMemberships.CountAsync());
        Assert.Equal(1, await fixture.Database.AuditEntries.CountAsync());
        Assert.Equal(1, await fixture.Database.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Failure_after_outbox_save_rolls_back_identity_idempotency_and_all_business_rows_without_replay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.Service(new FailingOutbox(fixture.Outbox));

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            service.ProvisionAsync(new("Rollback", null, "rollback-key", null)));

        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", failure.Code);
        Assert.False(failure.RequiresInspection);
        Assert.Null(failure.InnerException);
        Assert.Equal(1, fixture.Identity.EnsureCalls);
        Assert.Empty(fixture.Database.ChangeTracker.Entries());
        await fixture.AssertNoBusinessRowsAsync();
    }

    [Fact]
    public async Task Commit_failure_is_typed_uncertain_and_does_not_reinvoke_business_work()
    {
        var interceptor = new CommitRefusalInterceptor();
        await using var fixture = await Fixture.CreateAsync(interceptor);
        interceptor.Enabled = true;

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            fixture.Service().ProvisionAsync(new("Uncertain", null, "uncertain-key", null)));

        Assert.Equal("PROVISIONING_COMMIT_UNCERTAIN", failure.Code);
        Assert.True(failure.RequiresInspection);
        Assert.Equal(1, interceptor.CommitAttempts);
        Assert.Equal(1, fixture.Identity.EnsureCalls);
        Assert.Empty(fixture.Database.ChangeTracker.Entries());
        // This injection refuses before the provider commit. Real lost acknowledgments
        // remain an operational inspection gate, not an automatic retry permission.
        await fixture.AssertNoBusinessRowsAsync();
    }

    [Theory]
    [InlineData("code")]
    [InlineData("system")]
    public async Task Forged_owner_catalogue_is_refused_and_identity_creation_is_rolled_back(string changed)
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.Database.PermissionSets.SingleAsync(value => value.Id == PermissionSet.OrganisationOwnerId);
        if (changed == "code") fixture.Database.Entry(owner).Property(value => value.Code).CurrentValue = "NotOwner";
        else fixture.Database.Entry(owner).Property(value => value.IsSystem).CurrentValue = false;
        await fixture.Database.SaveChangesAsync();

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            fixture.Service().ProvisionAsync(new("Denied", null, "bad-owner-key", null)));

        Assert.Equal("PROVISIONING_OWNER_CATALOGUE_REJECTED", failure.Code);
        await fixture.AssertNoBusinessRowsAsync();
    }

    [Fact]
    public async Task C1_owner_with_nonempty_agreement_input_is_refused_without_recording_consent()
    {
        await using var fixture = await Fixture.CreateAsync();

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => fixture.Service().ProvisionAsync(
            new("No C1 consent", null, "no-consent", new[] { new AgreementConsent("synthetic-terms", "fixture-v1") })));

        Assert.Equal("PROVISIONING_OWNER_AGREEMENTS_REJECTED", failure.Message);
        Assert.Equal(0, fixture.Identity.EnsureCalls);
        await fixture.AssertNoBusinessRowsAsync();
    }

    [Fact]
    public async Task C2_verified_subject_path_is_not_enabled_by_the_owner_adapter()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Identity.Authority = ProvisioningAuthorityKind.VerifiedSubject;

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningException>(() =>
            fixture.Service().ProvisionAsync(new("No self service", null, "c2-not-enabled", null)));

        Assert.Equal("SELF_SERVICE_NOT_ENABLED", failure.Message);
        Assert.Equal(0, fixture.Identity.EnsureCalls);
        await fixture.AssertNoBusinessRowsAsync();
    }

    [Fact]
    public async Task Transaction_adapter_refuses_existing_caller_transaction_without_owning_or_committing_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var caller = await fixture.Database.Database.BeginTransactionAsync();
        var called = false;
        var adapter = new OrganisationProvisioningTransaction(fixture.Database);

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() => adapter.ExecuteAsync(_ =>
        {
            called = true;
            return Task.FromResult(new OrganisationProvisioningResult(OrganisationProvisioningOutcome.Created,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        }));

        Assert.Equal("PROVISIONING_CALLER_TRANSACTION_REJECTED", failure.Code);
        Assert.False(called);
        Assert.Same(caller, fixture.Database.Database.CurrentTransaction);
        await caller.RollbackAsync();
    }

    private sealed class CommitRefusalInterceptor : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int CommitAttempts { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
            {
                CommitAttempts++;
                throw new InvalidOperationException("Synthetic commit refusal.");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailingOutbox(IOutboxWriter inner) : IOutboxWriter
    {
        public async Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default)
        {
            await inner.EnqueueAsync(request, cancellationToken);
            throw new InvalidOperationException("Synthetic failure after persisted outbox write.");
        }
    }

    private sealed class MutableProvisioningClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture(IdentityTestContext context, AsyncServiceScope scope) : IAsyncDisposable
    {
        public AuthDbContext Database { get; } = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        public IOutboxWriter Outbox { get; } = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();
        public TrustedIdentity Identity { get; } = new(scope.ServiceProvider.GetRequiredService<AuthDbContext>(),
            scope.ServiceProvider.GetRequiredService<UserManager<User>>());
        public static async Task<Fixture> CreateAsync(DbTransactionInterceptor? interceptor = null, TimeProvider? timeProvider = null)
        {
            var context = await IdentityTestContext.CreateAsync(configureServices: services =>
            {
                if (timeProvider is not null) services.AddSingleton<TimeProvider>(timeProvider);
                if (interceptor is null) return;
                // AddDbContext uses the first options registration. Wrap it rather than
                // issuing a second registration whose options callback may never run.
                var original = services.Single(value => value.ServiceType == typeof(DbContextOptions<AuthDbContext>));
                var originalFactory = original.ImplementationFactory
                    ?? throw new InvalidOperationException("The existing Identity fixture options factory is required.");
                services.Remove(original);
                services.Add(new ServiceDescriptor(typeof(DbContextOptions<AuthDbContext>), provider =>
                {
                    var prior = (DbContextOptions<AuthDbContext>)originalFactory(provider);
                    return new DbContextOptionsBuilder<AuthDbContext>(prior).AddInterceptors(interceptor).Options;
                }, original.Lifetime));
            });
            var scope = context.Services.CreateAsyncScope();
            try
            {
                var fixture = new Fixture(context, scope);
                if (interceptor is not null)
                {
                    var configured = scope.ServiceProvider.GetRequiredService<DbContextOptions<AuthDbContext>>();
                    Assert.True(configured.FindExtension<CoreOptionsExtension>()?.Interceptors?.Any(value =>
                        ReferenceEquals(value, interceptor)) == true, "The exact commit interceptor must be configured.");
                    Assert.True(fixture.Database.Database.IsSqlite());
                    Assert.True(fixture.Database.Database.GetConnectionString() == context.Connection.ConnectionString,
                        "Interceptor registration must preserve the existing SQLite connection configuration.");
                }
                return fixture;
            }
            catch
            {
                await scope.DisposeAsync();
                await context.DisposeAsync();
                throw;
            }
        }
        public OrganisationProvisioningService Service(IOutboxWriter? outbox = null) => new(Identity,
            new OrganisationProvisioningTransaction(Database), new OrganisationProvisioningRepository(Database),
            scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(),
            scope.ServiceProvider.GetRequiredService<IAuditWriter>(), outbox ?? Outbox,
            scope.ServiceProvider.GetRequiredService<TimeProvider>(), new OrganisationProvisioningOptions
            {
                ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromDays(1)
            });
        public async Task AssertNoBusinessRowsAsync()
        {
            Database.ChangeTracker.Clear();
            Assert.Equal(0, await Database.Users.CountAsync());
            Assert.Equal(0, await Database.Organisations.CountAsync());
            Assert.Equal(0, await Database.OrganisationMemberships.CountAsync());
            Assert.Equal(0, await Database.IdempotencyRecords.CountAsync());
            Assert.Equal(0, await Database.AuditEntries.CountAsync());
            Assert.Equal(0, await Database.OutboxMessages.CountAsync());
        }
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); await context.DisposeAsync(); }
    }

    private sealed class TrustedIdentity(AuthDbContext database, UserManager<User> users) : IProvisioningIdentitySource
    {
        private readonly Guid subject = Guid.NewGuid();
        public string Password { get; } = Guid.NewGuid().ToString("N") + "aA1!";
        public int EnsureCalls { get; private set; }
        public ProvisioningAuthorityKind Authority { get; set; } = ProvisioningAuthorityKind.PrivilegedLocalOwner;
        public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProvisioningIdentity(subject, Authority));
        public async Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            Assert.NotNull(database.Database.CurrentTransaction);
            if (await database.Users.AnyAsync(value => value.Id == subject, cancellationToken)) return subject;
            // Trusted test fixture only. Actual owner attestation/SID admission is a
            // distinct CLI capability and is neither simulated nor proved here.
            var user = User.Create("synthetic@example.invalid", "synthetic", "Synthetic", "Owner", DateTimeOffset.UtcNow);
            user.Id = subject;
            user.EmailConfirmed = true;
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            database.Entry(user).Property(value => value.Status).CurrentValue = UserStatus.Active;
            await database.SaveChangesAsync(cancellationToken);
            return subject;
        }
    }
}
