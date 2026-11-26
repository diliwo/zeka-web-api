using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Application.Authorization;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Onboarding;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Authentication;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Infrastructure.IntegrationTests;

// Real PostgreSQL 17, production registration/verification, password sign-in, session records,
// RS256 issuer and the shared provisioning transaction. The signing key is disposable test material;
// this is provider proof, not a browser/HTTPS or deployed FileSigningKeyProvider witness.
[Collection(RegistrationPostgreSqlCollection.Name)]
[Trait("Issue", "ZEK-50")]
public sealed class BrowserOrganisationCreationPostgreSqlTests(RegistrationPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Genuine_session_creates_atomic_active_owner_and_persisted_replay_before_cap()
    {
        await using var test = await PrepareAsync();
        var command = Command("First", "stable-intent");
        var created = await test.CreateAsync(command);
        Assert.Equal(OrganisationProvisioningOutcome.Created, created.Outcome);
        var replay = await test.CreateAsync(command);
        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created, replay with { Outcome = created.Outcome });
        Assert.Equal(OrganisationProvisioningOutcome.Conflict,
            (await test.CreateAsync(command with { DisplayName = "Other" })).Outcome);
        var limited = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => test.CreateAsync(Command("Second", "new-intent")));
        Assert.Equal(BrowserOrganisationCreationCodes.LimitReached, limited.Code);
        await test.AssertSingleAsync(created);
        await using var db = test.Database();
        var owner = await db.OrganisationMemberships.SingleAsync();
        Assert.False(owner.Suspend(test.Clock.GetUtcNow()));
        Assert.False(owner.Leave(test.Clock.GetUtcNow()));
        Assert.Equal(MembershipStatus.Active, owner.Status);
        var selected = await new CurrentTenantAccessResolver(db, test.Clock).ResolveAsync(test.Subject, created.OrganisationId);
        Assert.Equal(TenantAccessOutcome.Authorized, selected.Outcome);
        Assert.Contains("Organisations.Export", selected.EffectivePermissionCodes);
        Assert.Contains("Organisations.Close", selected.EffectivePermissionCodes);
        Assert.Equal("First", selected.DisplayName);
        Assert.Null(selected.TimeZoneId);
        Assert.Equal(0, selected.TimeZoneConfigVersion);
        var eligible = await new EligibleOrganisationsResolver(db, test.Clock).ResolveAsync(test.Subject);
        Assert.Equal(created.OrganisationId, Assert.Single(eligible.Organisations).OrganisationId);
    }

    [Fact]
    public async Task Concurrent_same_key_on_separate_connections_creates_once_and_replays_once()
    {
        await using var test = await PrepareAsync();
        var command = Command("Concurrent", "same-key");
        var results = await Task.WhenAll(test.CreateAsync(command), test.CreateAsync(command));
        Assert.Single(results.Where(x => x.Outcome == OrganisationProvisioningOutcome.Created));
        Assert.Single(results.Where(x => x.Outcome == OrganisationProvisioningOutcome.Replay));
        Assert.Single(results.Select(x => x.OrganisationId).Distinct());
        await test.AssertSingleAsync(results[0]);
    }

    [Fact]
    public async Task Concurrent_distinct_keys_serialize_creator_lock_and_leave_no_failed_claim()
    {
        await using var test = await PrepareAsync();
        async Task<(OrganisationProvisioningResult? Result, string? Code)> Attempt(string key)
        {
            try { return (await test.CreateAsync(Command(key, key)), null); }
            catch (OrganisationProvisioningException error) { return (null, error.Code); }
        }
        var outcomes = await Task.WhenAll(Attempt("one"), Attempt("two"));
        var success = Assert.Single(outcomes.Where(x => x.Result is not null)).Result!;
        Assert.Equal(BrowserOrganisationCreationCodes.LimitReached, Assert.Single(outcomes.Where(x => x.Code is not null)).Code);
        await test.AssertSingleAsync(success);
    }

    [Fact]
    public async Task Configured_cap_counts_ownership_not_ordinary_membership()
    {
        await using var test = await PrepareAsync(maximum: 2);
        await test.AddOrdinaryMembershipAsync();
        var first = await test.CreateAsync(Command("First", "one"));
        var second = await test.CreateAsync(Command("Second", "two"));
        Assert.NotEqual(first.OrganisationId, second.OrganisationId);
        var error = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => test.CreateAsync(Command("Third", "three")));
        Assert.Equal(BrowserOrganisationCreationCodes.LimitReached, error.Code);
        await using var db = test.Database();
        Assert.Equal(2, await db.Organisations.CountAsync(x => x.OwnerUserId == test.Subject));
        Assert.Equal(2, await db.IdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_claim_organisation_membership_and_receipt_then_same_intent_can_retry()
    {
        var failure = new AuditFailure();
        await using var test = await PrepareAsync(interceptors: [failure]);
        failure.Enabled = true;
        var command = Command("Rollback", "rollback");
        var error = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => test.CreateAsync(command));
        Assert.Equal(BrowserOrganisationCreationCodes.Unavailable, error.Code);
        await using (var db = test.Database())
        {
            Assert.Equal(0, await db.Organisations.CountAsync());
            Assert.Equal(0, await db.OrganisationMemberships.CountAsync());
            Assert.Equal(0, await db.IdempotencyRecords.CountAsync());
            Assert.Equal(0, await db.AuditEntries.CountAsync(x => x.Action == "organisation.provisioned"));
        }
        failure.Enabled = false;
        await test.AssertSingleAsync(await test.CreateAsync(command));
    }

    [Fact]
    public async Task Lost_commit_acknowledgment_reports_uncertainty_and_fresh_context_reconciles_same_intent_without_replay()
    {
        var failure = new LostCommitAcknowledgment();
        await using var test = await PrepareAsync(interceptors: [failure]);
        failure.Enabled = true;
        var command = Command("Durable", "uncertain");
        var error = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => test.CreateAsync(command));
        Assert.Equal(BrowserOrganisationCreationCodes.ResultUncertain, error.Code);
        Assert.Equal(1, failure.Attempts);
        failure.Enabled = false;
        var recovered = await test.CreateAsync(command);
        Assert.Equal(OrganisationProvisioningOutcome.Replay, recovered.Outcome);
        await test.AssertSingleAsync(recovered);
    }

    [Theory]
    [InlineData("session")] [InlineData("user")] [InlineData("verification")]
    public async Task Current_identity_or_session_revocation_refuses_even_a_successful_replay(string change)
    {
        await using var test = await PrepareAsync();
        var command = Command("Existing", "current-authority");
        await test.CreateAsync(command);
        await using (var db = test.Database())
        {
            if (change == "session") (await db.BrowserSessionFamilies.SingleAsync()).RevokedAtUtc = test.Clock.GetUtcNow();
            else
            {
                var user = await db.Users.SingleAsync();
                if (change == "user") db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.PendingVerification;
                else user.EmailConfirmed = false;
            }
            await db.SaveChangesAsync();
        }
        var error = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => test.CreateAsync(command));
        Assert.Equal(BrowserOrganisationCreationCodes.Unauthenticated, error.Code);
        await using var read = test.Database();
        Assert.Equal(1, await read.Organisations.CountAsync());
        Assert.Equal(1, await read.IdempotencyRecords.CountAsync());
    }

    [Fact]
    public async Task Existing_processing_intent_is_not_completed_early_and_expired_claim_recovers_atomically()
    {
        await using var test = await PrepareAsync();
        var command = Command("Recoverable", "processing");
        var canonical = JsonSerializer.Serialize(new { schemaVersion = OrganisationProvisioningService.Operation,
            subjectId = test.Subject, authorityKind = ProvisioningAuthorityKind.VerifiedSubject.ToString(),
            displayName = command.DisplayName.Trim(), slug = (string?)null,
            agreementStatus = OrganisationProvisioningAudit.LocalM1DeferredAgreements });
        using (var scope = test.Services.CreateScope())
            Assert.Equal(IdempotencyDecisionKind.Started, (await scope.ServiceProvider.GetRequiredService<IIdempotencyStore>()
                .TryBeginAsync(test.Subject, OrganisationProvisioningService.Operation, command.IdempotencyKey,
                    canonical, TimeSpan.FromMinutes(2), TimeSpan.FromDays(1))).Kind);
        Assert.Equal(OrganisationProvisioningOutcome.Processing, (await test.CreateAsync(command)).Outcome);
        test.Clock.Advance(TimeSpan.FromMinutes(3));
        await test.AssertSingleAsync(await test.CreateAsync(command));
    }

    private static OrganisationProvisioningCommand Command(string name, string key) => new(name, null, key, null);

    private async Task<TestHost> PrepareAsync(int maximum = 1, params IInterceptor[] interceptors)
    {
        var registration = await fixture.PrepareAsync();
        RegistrationVerificationKeyFixture? sessionKey = null;
        ServiceProvider? services = null;
        try
        {
            var email = "creator-" + Guid.NewGuid().ToString("N") + "@example.invalid";
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA1!";
            Assert.Equal(RegistrationOutcome.Accepted, await registration.Run(service => service.RegisterAsync(
                new RegistrationRequest { Email = email, Password = password, FirstName = "Synthetic", LastName = "Creator" }, default)));
            var verificationToken = await registration.CurrentToken();
            Assert.Equal(RegistrationOutcome.Accepted, (await registration.Run(service => service.ExchangeAsync(verificationToken, default))).Outcome);
            sessionKey = RegistrationVerificationKeyFixture.Create(null);
            var builder = fixture.CreateBuilder(registration.Runtime, registration.Clock, interceptors);
            builder.Configuration[BrowserOrganisationCreationPolicy.SectionName + ":Enabled"] = "true";
            builder.Configuration[BrowserOrganisationCreationPolicy.SectionName + ":MaxOwnedOrganisations"] = maximum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
            builder.Services.AddSingleton(new BrowserSessionPolicy { HashKeyReference = sessionKey.Path,
                AccessLifetime = TimeSpan.FromMinutes(30), IdleLifetime = TimeSpan.FromHours(1), AbsoluteLifetime = TimeSpan.FromHours(4) });
            builder.Services.AddSingleton<ISessionAccessTokenIssuer, TokenIssuer>();
            builder.Services.AddScoped<BrowserSessions>();
            builder.Services.AddScoped<IBrowserSessions>(provider => provider.GetRequiredService<BrowserSessions>());
            builder.Services.AddBrowserOrganisationCreation(builder.Configuration, builder.Environment);
            services = builder.Services.BuildServiceProvider();
            using var scope = services.CreateScope();
            var signedIn = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().SignInAsync(email, password, null, default);
            Assert.Equal(BrowserSessionOutcome.Success, signedIn.Outcome);
            var jwt = new JsonWebToken(signedIn.AccessToken!);
            Assert.Equal("RS256", jwt.Alg);
            Assert.Equal(signedIn.SubjectId, Guid.Parse(jwt.Subject));
            var sid = Guid.Parse(jwt.GetClaim("sid").Value);
            return new(registration, services, sessionKey, signedIn.SubjectId, sid);
        }
        catch
        {
            if (services is not null) await services.DisposeAsync();
            sessionKey?.Dispose();
            await registration.DisposeAsync();
            throw;
        }
    }

    private sealed class TestHost(RegistrationPostgreSqlFixture.RegistrationFixtureHost registration,
        ServiceProvider services, RegistrationVerificationKeyFixture sessionKey, Guid subject, Guid session) : IAsyncDisposable
    {
        public Guid Subject => subject;
        public ServiceProvider Services => services;
        public RegistrationPostgreSqlFixture.FixtureClock Clock => registration.Clock;
        public AuthDbContext Database() => registration.Read();
        public async Task<OrganisationProvisioningResult> CreateAsync(OrganisationProvisioningCommand command)
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IBrowserOrganisationCreation>().CreateAsync(subject, session, command);
        }
        public async Task AddOrdinaryMembershipAsync()
        {
            var email = "other-" + Guid.NewGuid().ToString("N") + "@example.invalid";
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "aA1!";
            Assert.Equal(RegistrationOutcome.Accepted, await registration.Run(service => service.RegisterAsync(
                new RegistrationRequest { Email = email, Password = password, FirstName = "Synthetic", LastName = "Other" }, default)));
            var token = await registration.CurrentToken();
            Assert.Equal(RegistrationOutcome.Accepted, (await registration.Run(service => service.ExchangeAsync(token, default))).Outcome);
            await using var db = Database();
            var other = await db.Users.SingleAsync(x => x.Email == email);
            var now = Clock.GetUtcNow();
            var organisation = Organisation.Create(Guid.NewGuid(), "Other owner's organisation", other.Id, now);
            Assert.True(organisation.Activate(now));
            await using var transaction = await db.Database.BeginTransactionAsync();
            await TenantContextInitializer.InitializeAsync(db, transaction, organisation.Id, default);
            db.AddRange(organisation, OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, other.Id, now),
                OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, subject, PermissionSet.OrganisationAdministratorId, now));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        public async Task AssertSingleAsync(OrganisationProvisioningResult result)
        {
            await using var db = Database();
            var organisation = await db.Organisations.SingleAsync();
            var membership = await db.OrganisationMemberships.SingleAsync();
            Assert.Equal(subject, organisation.OwnerUserId);
            Assert.Equal(OrganisationStatus.Active, organisation.Status);
            Assert.Equal(result.OrganisationId, organisation.Id);
            Assert.Equal(result.MembershipId, membership.Id);
            Assert.Equal(subject, membership.UserId);
            Assert.Equal(organisation.Id, membership.OrganisationId);
            Assert.Equal(PermissionSet.OrganisationOwnerId, membership.PermissionSetId);
            Assert.Equal(MembershipStatus.Active, membership.Status);
            Assert.Null(organisation.TimeZoneId);
            Assert.Equal(0, organisation.TimeZoneConfigVersion);
            Assert.Equal(IdempotencyStatus.Completed, (await db.IdempotencyRecords.SingleAsync()).Status);
            var audit = await db.AuditEntries.SingleAsync(x => x.Action == "organisation.provisioned");
            Assert.Contains(OrganisationProvisioningAudit.LocalM1DeferredAgreements, audit.Metadata);
            Assert.DoesNotContain(OrganisationProvisioningAudit.OperatorNoAgreements, audit.Metadata);
            Assert.Equal(0, await db.OutboxMessages.CountAsync());
        }
        public async ValueTask DisposeAsync()
        {
            try { await services.DisposeAsync(); }
            finally { try { sessionKey.Dispose(); } finally { await registration.DisposeAsync(); } }
        }
    }

    private sealed class TokenIssuer(TimeProvider clock) : ISessionAccessTokenIssuer, IDisposable
    {
        private readonly Zeka.Authentication.Tests.AuthenticationFixture fixture = new();
        public async Task<string> IssueSessionAsync(Guid subjectId, Guid sessionId, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            var issuer = new AccessTokenIssuer(new IssuerContractTests.TestKeys(fixture.Key), new IssuerOptions
            {
                ActiveKeyId = Zeka.Authentication.Tests.AuthenticationFixture.KeyId,
                ActiveSigningKeyReference = Path.Combine(Path.GetTempPath(), "not-provisioned-session-test"), AccessTokenLifetime = lifetime
            }, Zeka.Authentication.AuthenticationOptions.Read(fixture.Configuration()), clock);
            issuer.ConfirmPublication(await issuer.PrepareAsync(cancellationToken));
            return await issuer.IssueSessionAsync(subjectId, sessionId, lifetime, cancellationToken);
        }
        public void Dispose() => fixture.Dispose();
    }

    private sealed class AuditFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<AuditEntry>().Any(x => x.State == EntityState.Added))
                throw new InvalidOperationException("Synthetic audit persistence failure");
            return ValueTask.FromResult(result);
        }
    }
    private sealed class LostCommitAcknowledgment : DbTransactionInterceptor
    {
        public bool Enabled { get; set; }
        public int Attempts { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Enabled) { Attempts++; throw new IOException("Synthetic lost commit acknowledgment"); }
            return Task.CompletedTask;
        }
    }
}