using System.Security.Cryptography;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Provisioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Zeka.AuthOwnerProvision;

namespace Zeka.AuthOwnerProvision.Tests;

// Real Identity/UserManager and actual shared application/transaction adapters,
// against isolated SQLite memory only. This does not prove Windows attestation,
// PostgreSQL runtime privileges/locks, image admission, or any live operation.
public sealed class OwnerIdentitySourceTests
{
    private static readonly string InitialPassword = CreateFixturePassword();
    private static readonly string DifferentPassword = CreateFixturePassword(InitialPassword);

    [Fact]
    public async Task Actual_admitted_identity_is_created_confirmed_and_active_through_shared_service_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service().ProvisionAsync(Command());

        Assert.Equal(OrganisationProvisioningOutcome.Created, created.Outcome);
        Assert.Equal(fixture.Subject, created.SubjectId);
        fixture.Database.ChangeTracker.Clear();
        var user = await fixture.Database.Users.SingleAsync();
        Assert.Equal(fixture.Subject, user.Id);
        Assert.Equal(OwnerProtocol.ApprovedEmail, user.NormalizedEmail);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(fixture.Clock.GetUtcNow(), user.CreatedAtUtc);
        Assert.Equal(user.CreatedAtUtc, user.UpdatedAtUtc);
        Assert.True(await fixture.Users.CheckPasswordAsync(user, InitialPassword));
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));
        Assert.False(string.IsNullOrWhiteSpace(user.SecurityStamp));
        Assert.Equal(created.SubjectId, (await fixture.Database.Organisations.SingleAsync()).OwnerUserId);
        var membership = await fixture.Database.OrganisationMemberships.SingleAsync();
        Assert.Equal(created.MembershipId, membership.Id);
        Assert.Equal(created.OrganisationId, membership.OrganisationId);
        Assert.Equal(created.SubjectId, membership.UserId);
        await fixture.AssertCountsAsync(1, 1);
        var audit = await fixture.Database.AuditEntries.SingleAsync();
        Assert.Contains(OrganisationProvisioningAudit.OperatorNoAgreements, audit.Metadata);
        Assert.Contains(nameof(ProvisioningAuthorityKind.PrivilegedLocalOwner), audit.Metadata);
        Assert.DoesNotContain(InitialPassword, audit.Metadata);
        var outbox = await fixture.Database.OutboxMessages.SingleAsync();
        Assert.DoesNotContain(InitialPassword, outbox.Payload);
        Assert.Equal(0, await fixture.Database.BrowserSessionFamilies.CountAsync());
    }

    [Fact]
    public async Task Persisted_replay_keeps_credentials_and_ids_even_when_an_unused_input_password_differs()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service().ProvisionAsync(Command());
        fixture.Database.ChangeTracker.Clear();
        var before = await fixture.Database.Users.AsNoTracking().SingleAsync();
        var passwordHash = before.PasswordHash;
        var securityStamp = before.SecurityStamp;

        var replay = await fixture.Service(DifferentPassword).ProvisionAsync(Command());

        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created.SubjectId, replay.SubjectId);
        Assert.Equal(created.OrganisationId, replay.OrganisationId);
        Assert.Equal(created.MembershipId, replay.MembershipId);
        fixture.Database.ChangeTracker.Clear();
        var after = await fixture.Database.Users.SingleAsync();
        Assert.True(string.Equals(passwordHash, after.PasswordHash, StringComparison.Ordinal));
        Assert.True(string.Equals(securityStamp, after.SecurityStamp, StringComparison.Ordinal));
        Assert.True(await fixture.Users.CheckPasswordAsync(after, InitialPassword));
        Assert.False(await fixture.Users.CheckPasswordAsync(after, DifferentPassword));
        await fixture.AssertCountsAsync(1, 1);
    }

    [Fact]
    public async Task Existing_matching_confirmed_owner_is_reused_without_applying_the_unused_input_password()
    {
        await using var fixture = await Fixture.CreateAsync();
        var email = OwnerProtocol.ApprovedEmail.ToLowerInvariant();
        var existing = User.Create(email, email, "Unit", "Fixture", fixture.Clock.GetUtcNow());
        existing.Id = fixture.Subject;
        existing.EmailConfirmed = true;
        existing.ActivateConfirmedIdentity(fixture.Clock.GetUtcNow());
        Assert.True((await fixture.Users.CreateAsync(existing, InitialPassword)).Succeeded);
        var priorHash = existing.PasswordHash;
        var priorStamp = existing.SecurityStamp;
        fixture.Database.ChangeTracker.Clear();

        var result = await fixture.Service(DifferentPassword).ProvisionAsync(Command());

        Assert.Equal(OrganisationProvisioningOutcome.Created, result.Outcome);
        Assert.Equal(fixture.Subject, result.SubjectId);
        fixture.Database.ChangeTracker.Clear();
        var preserved = await fixture.Database.Users.SingleAsync();
        Assert.True(string.Equals(priorHash, preserved.PasswordHash, StringComparison.Ordinal));
        Assert.True(string.Equals(priorStamp, preserved.SecurityStamp, StringComparison.Ordinal));
        Assert.True(await fixture.Users.CheckPasswordAsync(preserved, InitialPassword));
        Assert.False(await fixture.Users.CheckPasswordAsync(preserved, DifferentPassword));
        await fixture.AssertCountsAsync(1, 1);
    }
    [Theory]
    [InlineData("unconfirmed")]
    [InlineData("wrong-subject")]
    [InlineData("wrong-email")]
    public async Task Actual_identity_source_refuses_existing_unconfirmed_or_foreign_owner_without_password_reset(string mismatch)
    {
        await using var fixture = await Fixture.CreateAsync();
        var id = mismatch == "wrong-subject" ? Guid.NewGuid() : fixture.Subject;
        var email = mismatch == "wrong-email" ? "different-owner@example.invalid" : OwnerProtocol.ApprovedEmail.ToLowerInvariant();
        var user = User.Create(email, email, "Unit", "Fixture", fixture.Clock.GetUtcNow());
        user.Id = id;
        if (mismatch != "unconfirmed")
        {
            user.EmailConfirmed = true;
            user.ActivateConfirmedIdentity(fixture.Clock.GetUtcNow());
        }
        Assert.True((await fixture.Users.CreateAsync(user, InitialPassword)).Succeeded);
        var priorHash = user.PasswordHash;
        var priorStamp = user.SecurityStamp;
        fixture.Database.ChangeTracker.Clear();

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            fixture.Service(DifferentPassword).ProvisionAsync(Command()));

        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", failure.Code);
        Assert.False(failure.RequiresInspection);
        Assert.Null(failure.InnerException);
        fixture.Database.ChangeTracker.Clear();
        var preserved = await fixture.Database.Users.SingleAsync();
        Assert.Equal(id, preserved.Id);
        Assert.True(string.Equals(priorHash, preserved.PasswordHash, StringComparison.Ordinal));
        Assert.True(string.Equals(priorStamp, preserved.SecurityStamp, StringComparison.Ordinal));
        Assert.Equal(mismatch != "unconfirmed", preserved.EmailConfirmed);
        await fixture.AssertCountsAsync(1, 0);
    }

    [Fact]
    public async Task Failure_after_real_identity_and_outbox_persistence_rolls_back_every_operation_row()
    {
        await using var fixture = await Fixture.CreateAsync();
        var injected = new FailAfterSavedOutbox(fixture.Outbox, fixture.Database);

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            fixture.Service(outbox: injected).ProvisionAsync(Command()));

        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", failure.Code);
        Assert.False(failure.RequiresInspection);
        Assert.Null(failure.InnerException);
        Assert.Equal(1, injected.Calls);
        Assert.True(injected.IdentityWasPresent);
        Assert.Empty(fixture.Database.ChangeTracker.Entries());
        await fixture.AssertCountsAsync(0, 0);
    }

    [Fact]
    public async Task Normal_Identity_password_policy_is_enforced_and_weak_input_rolls_back_without_user_creation()
    {
        await using var fixture = await Fixture.CreateAsync();

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            fixture.Service("weak").ProvisionAsync(Command()));

        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", failure.Code);
        await fixture.AssertCountsAsync(0, 0);
    }

    [Fact]
    public async Task Describing_attestation_does_not_create_identity_and_ensuring_requires_shared_transaction()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Identity();
        var admitted = await source.DescribeAsync();

        Assert.Equal(fixture.Subject, admitted.SubjectId);
        Assert.Equal(ProvisioningAuthorityKind.PrivilegedLocalOwner, admitted.AuthorityKind);
        var failure = await Assert.ThrowsAsync<OwnerToolException>(() => source.EnsureConfirmedAsync(admitted));
        Assert.Equal("OWNER_IDENTITY_REJECTED", failure.Code);
        await fixture.AssertCountsAsync(0, 0);
    }

    private static string CreateFixturePassword(string? distinctFrom = null)
    {
        string value;
        do
        {
            value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "Aa1!";
        }
        while (string.Equals(value, distinctFrom, StringComparison.Ordinal));
        return value;
    }
    private static OrganisationProvisioningCommand Command() =>
        new("Unit fixture organisation", null, "stable-owner-unit-fixture", null);

    private sealed class FailAfterSavedOutbox(IOutboxWriter inner, AuthDbContext database) : IOutboxWriter
    {
        public int Calls { get; private set; }
        public bool IdentityWasPresent { get; private set; }
        public async Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            await inner.EnqueueAsync(request, cancellationToken);
            IdentityWasPresent = await database.Users.AnyAsync(cancellationToken);
            throw new InvalidOperationException("Unit fixture failure after persisted outbox.");
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Fixture(SqliteConnection connection, ServiceProvider services, AsyncServiceScope scope) : IAsyncDisposable
    {
        public Guid Subject { get; } = Guid.NewGuid();
        public FixedClock Clock { get; } = (FixedClock)scope.ServiceProvider.GetRequiredService<TimeProvider>();
        public AuthDbContext Database { get; } = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        public UserManager<User> Users { get; } = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        public IOutboxWriter Outbox { get; } = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection($"Data Source=owner-identity-unit-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await connection.OpenAsync();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite(connection.ConnectionString));
            builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
            builder.ConfigureMicrosoftIdentity();
            builder.Services.AddSingleton<TimeProvider>(new FixedClock());
            var services = builder.Services.BuildServiceProvider();
            var scope = services.CreateAsyncScope();
            try
            {
                var fixture = new Fixture(connection, services, scope);
                await fixture.Database.Database.EnsureCreatedAsync();
                return fixture;
            }
            catch
            {
                await scope.DisposeAsync();
                await services.DisposeAsync();
                await connection.DisposeAsync();
                throw;
            }
        }

        public OwnerIdentitySource Identity(string? password = null) =>
            new(Admitted(password ?? InitialPassword), Users, Database, Clock);

        public OrganisationProvisioningService Service(string? password = null, IOutboxWriter? outbox = null) =>
            new(Identity(password), new OrganisationProvisioningTransaction(Database),
                new OrganisationProvisioningRepository(Database),
                scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(),
                scope.ServiceProvider.GetRequiredService<IAuditWriter>(), outbox ?? Outbox,
                Clock, new OrganisationProvisioningOptions
                {
                    ProcessingTimeout = TimeSpan.FromSeconds(120), Retention = TimeSpan.FromSeconds(86400)
                });

        private AdmittedOwnerInput Admitted(string password)
        {
            var source = new string('a', 40);
            var binary = new string('b', 64);
            var request = new OwnerInput
            {
                Schema = OwnerProtocol.InputSchema, Scope = "LocalSyntheticM1OwnerProvision",
                Project = "zeka-m1", AuthorityKind = "OwnerAttestedLocalSynthetic",
                OwnerDecisionId = OwnerProtocol.OwnerDecision, ApprovalCommentId = "6529021741",
                OwnerAccount = OwnerProtocol.ApprovedOwner,
                ExpectedOwnerSid = "S-1-5-21-1-2-3-1001", ActualOwnerSid = "S-1-5-21-1-2-3-1001",
                ExpectedOwnerProfile = OwnerProtocol.ApprovedProfile, ActualOwnerProfile = OwnerProtocol.ApprovedProfile,
                NormalizedEmail = OwnerProtocol.ApprovedEmail,
                SourceCommit = source, BinarySha256 = binary, DatabaseSourceCommit = new string('c', 40),
                DatabaseName = "zeka_m1_auth", InitializationId = "unit-fixture-initialization-0001",
                SubjectId = Subject, DisplayName = "Unit fixture organisation", Slug = null,
                IdempotencyKey = "stable-owner-unit-fixture", ProcessingTimeoutSeconds = 120, RetentionSeconds = 86400,
                AuthRuntimeConnectionString = "Host=postgres;Database=zeka_m1_auth;Username=zeka_auth_runtime;Password=unit_fixture_not_live",
                OwnerPassword = password
            };
            return OwnerProtocol.Admit(request, new(source, "0.1.0+" + source, binary));
        }

        public async Task AssertCountsAsync(int users, int operations)
        {
            Database.ChangeTracker.Clear();
            Assert.Equal(users, await Database.Users.CountAsync());
            Assert.Equal(operations, await Database.Organisations.CountAsync());
            Assert.Equal(operations, await Database.OrganisationMemberships.CountAsync());
            Assert.Equal(operations, await Database.IdempotencyRecords.CountAsync());
            Assert.Equal(operations, await Database.AuditEntries.CountAsync());
            Assert.Equal(operations, await Database.OutboxMessages.CountAsync());
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await services.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
