using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AuthManager.Application.Onboarding;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Onboarding;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RegistrationPostgreSqlCollection : ICollectionFixture<RegistrationPostgreSqlFixture>
{
    public const string Name = "ZEK-27 registration PostgreSQL";
}

[Collection(RegistrationPostgreSqlCollection.Name)]
public sealed class RegistrationPostgreSqlTests(RegistrationPostgreSqlFixture fixture)
{
    [Fact]
    public async Task Concurrent_registration_has_one_identity_challenge_mail_and_witness()
    {
        await using var test = await fixture.PrepareAsync();
        var input = Input();
        var first = test.Run(service => service.RegisterAsync(input, CancellationToken.None));
        var second = test.Run(service => service.RegisterAsync(input, CancellationToken.None));
        Assert.All(await Task.WhenAll(first, second), status => Assert.Equal(RegistrationOutcome.Accepted, status));
        await using var db = test.Read();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.EmailVerificationChallenges.CountAsync());
        Assert.Equal(1, await db.VerificationMailOutboxMessages.CountAsync());
        Assert.Equal(1, await db.OnboardingOperationReceipts.CountAsync());
        Assert.False((await db.Users.SingleAsync()).EmailConfirmed);
    }

    [Fact]
    public async Task Failure_after_Identity_autosave_rolls_back_all_enrollment_writes()
    {
        var fail = new FailChallengeWrite();
        await using var test = await fixture.PrepareAsync(fail);
        var input = Input();
        Assert.Equal(RegistrationOutcome.Unavailable,
            await test.Run(service => service.RegisterAsync(input, CancellationToken.None)));
        Assert.True(fail.SawIdentitySave);
        await using var db = test.Read();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.EmailVerificationChallenges.CountAsync());
        Assert.Equal(0, await db.VerificationMailOutboxMessages.CountAsync());
        Assert.Equal(0, await db.OnboardingOperationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registration_reconciles_acknowledgement_loss_and_replays_a_verified_rollback(bool afterCommit)
    {
        var fault = new CommitFault(afterCommit);
        await using var test = await fixture.PrepareAsync(fault);
        var input = Input();
        Assert.Equal(RegistrationOutcome.Accepted,
            await test.Run(service => service.RegisterAsync(input, CancellationToken.None)));
        Assert.Equal(1, fault.Failures);
        await using var db = test.Read();
        Assert.Equal(1, await db.Users.CountAsync());
        Assert.Equal(1, await db.EmailVerificationChallenges.CountAsync());
        Assert.Equal(1, await db.VerificationMailOutboxMessages.CountAsync());
        Assert.Equal(1, await db.OnboardingOperationReceipts.CountAsync());
    }

    [Fact]
    public async Task Parallel_exchange_and_replay_produce_exactly_one_restricted_grant()
    {
        await using var test = await fixture.PrepareAsync();
        var input = Input();
        Assert.Equal(RegistrationOutcome.Accepted,
            await test.Run(service => service.RegisterAsync(input, CancellationToken.None)));
        var token = await test.CurrentToken();
        var left = test.Run(service => service.ExchangeAsync(token, CancellationToken.None));
        var right = test.Run(service => service.ExchangeAsync(token, CancellationToken.None));
        var results = await Task.WhenAll(left, right);
        var success = Assert.Single(results.Where(result => result.Outcome == RegistrationOutcome.Accepted));
        Assert.Single(results.Where(result => result.Outcome == RegistrationOutcome.Invalid));
        Assert.True(success.Credential is { Length: 43 });
        Assert.Equal(RegistrationOutcome.Invalid,
            (await test.Run(service => service.ExchangeAsync(token, CancellationToken.None))).Outcome);

        await using var db = test.Read();
        var grant = await db.OnboardingGrants.SingleAsync();
        var challenge = await db.EmailVerificationChallenges.SingleAsync();
        Assert.Equal(VerificationChallengeState.Consumed, challenge.Status);
        Assert.Equal(UserStatus.Active, (await db.Users.SingleAsync()).Status);
        Assert.Equal(TimeSpan.FromMinutes(10), grant.ExpiresAtUtc - test.Clock.GetUtcNow());
        Assert.True(grant.CredentialHash == VerificationTokenService.GrantHash(success.Credential!));
        Assert.True(await test.Run(service => service.ValidateGrantAsync(success.Credential!,
            OnboardingGrant.OrganisationPurpose, CancellationToken.None)) == grant.UserId);
        Assert.Null(await test.Run(service => service.ValidateGrantAsync(success.Credential!,
            "TenantSession", CancellationToken.None)));
        Assert.Equal(0, await db.BrowserSessionFamilies.CountAsync());
        Assert.Equal(0, await db.OrganisationMemberships.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exchange_commit_loss_emits_only_a_verified_grant(bool canVerify)
    {
        var fault = new CommitFault(afterCommit: true, armed: false);
        var failReceipt = new ReceiptFault();
        await using var test = await fixture.PrepareAsync(fault, failReceipt);
        Assert.Equal(RegistrationOutcome.Accepted,
            await test.Run(service => service.RegisterAsync(Input(), CancellationToken.None)));
        var token = await test.CurrentToken();
        fault.Armed = true;
        failReceipt.Armed = !canVerify;
        var exchange = await test.Run(service => service.ExchangeAsync(token, CancellationToken.None));
        Assert.Equal(canVerify ? RegistrationOutcome.Accepted : RegistrationOutcome.Unavailable, exchange.Outcome);
        Assert.Equal(canVerify, exchange.Credential is not null);
        Assert.Equal(1, fault.Failures);
        Assert.Equal(canVerify ? 0 : 1, failReceipt.Failures);
        Assert.Equal(RegistrationOutcome.Invalid,
            (await test.Run(service => service.ExchangeAsync(token, CancellationToken.None))).Outcome);
        await using var db = test.Read();
        Assert.Equal(1, await db.OnboardingGrants.CountAsync());
        Assert.Equal(2, await db.OnboardingOperationReceipts.CountAsync());
    }

    [Fact]
    public async Task Resend_cooldown_hour_and_day_caps_preserve_single_pending_challenge()
    {
        await using var test = await fixture.PrepareAsync();
        var input = Input();
        await test.Run(service => service.RegisterAsync(input, CancellationToken.None));
        var original = await test.CurrentToken();
        await test.Run(service => service.RegisterAsync(Input(input.Email!), CancellationToken.None));
        await test.Run(service => service.ResendAsync(input.Email!, CancellationToken.None));
        Assert.True(original == await test.CurrentToken());

        test.Clock.Advance(TimeSpan.FromMinutes(1));
        var first = test.Run(service => service.ResendAsync(input.Email!, CancellationToken.None));
        var second = test.Run(service => service.ResendAsync(input.Email!, CancellationToken.None));
        Assert.All(await Task.WhenAll(first, second), status => Assert.Equal(RegistrationOutcome.Accepted, status));
        Assert.True(original != await test.CurrentToken());
        Assert.Equal(RegistrationOutcome.Invalid,
            (await test.Run(service => service.ExchangeAsync(original, CancellationToken.None))).Outcome);
        for (var index = 0; index < 4; index++)
        {
            test.Clock.Advance(TimeSpan.FromMinutes(1));
            await test.Run(service => service.ResendAsync(input.Email!, CancellationToken.None));
        }
        await using (var db = test.Read())
            Assert.Equal(3, await db.VerificationMailOutboxMessages.CountAsync());
        for (var index = 0; index < 4; index++)
        {
            test.Clock.Advance(TimeSpan.FromHours(1));
            await test.Run(service => service.ResendAsync(input.Email!, CancellationToken.None));
        }
        await using (var db = test.Read())
        {
            Assert.Equal(5, await db.VerificationMailOutboxMessages.CountAsync());
            Assert.Equal(1, await db.EmailVerificationChallenges.CountAsync(challenge =>
                challenge.Status == VerificationChallengeState.Pending));
        }
    }

    [Fact]
    public async Task Exact_expiry_is_denied_and_no_tenant_records_or_grant_are_created()
    {
        await using var test = await fixture.PrepareAsync();
        await test.Run(service => service.RegisterAsync(Input(), CancellationToken.None));
        var token = await test.CurrentToken();
        test.Clock.Advance(TimeSpan.FromHours(24));
        Assert.Equal(RegistrationOutcome.Invalid,
            (await test.Run(service => service.ExchangeAsync(token, CancellationToken.None))).Outcome);
        await using var db = test.Read();
        Assert.Equal(0, await db.OnboardingGrants.CountAsync());
        Assert.Equal(0, await db.OrganisationMemberships.CountAsync());
        Assert.Equal(0, await db.BrowserSessionFamilies.CountAsync());
    }


    [Fact]
    public async Task Failure_after_provider_returns_the_enrollment_insert_batch_rolls_back_every_row()
    {
        var fail = new FailAfterInsertBatch();
        await using var test = await fixture.PrepareAsync(fail);
        Assert.Equal(RegistrationOutcome.Unavailable,
            await test.Run(service => service.RegisterAsync(Input(), CancellationToken.None)));
        Assert.Equal(1, fail.Executions);
        await using var db = test.Read();
        Assert.Equal(0, await db.Users.CountAsync());
        Assert.Equal(0, await db.EmailVerificationChallenges.CountAsync());
        Assert.Equal(0, await db.VerificationMailOutboxMessages.CountAsync());
        Assert.Equal(0, await db.OnboardingOperationReceipts.CountAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revoked_challenge_or_suspended_identity_cannot_exchange(bool revokeChallenge)
    {
        await using var test = await fixture.PrepareAsync();
        Assert.Equal(RegistrationOutcome.Accepted,
            await test.Run(service => service.RegisterAsync(Input(), CancellationToken.None)));
        var token = await test.CurrentToken();
        await using (var db = test.Read())
        {
            if (revokeChallenge)
                (await db.EmailVerificationChallenges.SingleAsync()).Transition(
                    VerificationChallengeState.Revoked, test.Clock.GetUtcNow(), Guid.NewGuid());
            else
            {
                var user = await db.Users.SingleAsync();
                db.Entry(user).Property(record => record.Status).CurrentValue = UserStatus.Suspended;
            }
            await db.SaveChangesAsync();
        }
        var result = await test.Run(service => service.ExchangeAsync(token, CancellationToken.None));
        Assert.Equal(RegistrationOutcome.Invalid, result.Outcome);
        Assert.Null(result.Credential);
        await using var check = test.Read();
        Assert.Equal(0, await check.OnboardingGrants.CountAsync());
    }

    [Theory]
    [InlineData("Register", "BeforeIdentity", true)]
    [InlineData("Register", "AfterIdentitySave", false)]
    [InlineData("Register", "AfterFinalSave", false)]
    [InlineData("Resend", "BeforeIdentity", false)]
    [InlineData("Resend", "AfterIdentitySave", false)]
    [InlineData("Resend", "AfterFinalSave", false)]
    [InlineData("Exchange", "BeforeIdentity", false)]
    [InlineData("Exchange", "AfterIdentitySave", false)]
    [InlineData("Exchange", "AfterFinalSave", false)]
    public async Task Cancellation_before_commit_preserves_the_durable_enrollment(string action, string boundary, bool clientAbort)
    {
        using var deadline = new CancellationTokenSource();
        using var aborted = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, aborted.Token);
        var witness = new CancellationWitness(boundary, clientAbort ? aborted : deadline, linked.Token);
        await using var test = await fixture.PrepareAsync(new ReadBoundary(witness), new SaveBoundary(witness), new CommitBoundary(witness));
        var input = Input();
        var token = await SeedForAsync(test, action, input);
        var previous = await ReadEnrollmentAsync(test);

        var accessor = test.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        try
        {
            await using var scope = test.Services.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var service = services.GetRequiredService<IRegistrationService>();
            witness.Armed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => ApplyAsync(service, action, input, token, linked.Token));
            Assert.Equal(1, witness.Cancellations);
            Assert.Equal(1, witness.Transactions);
            Assert.Equal(0, witness.CommandsAfterCancellation);
            Assert.Equal(0, witness.CommitAttempts);
            Assert.Equal(clientAbort, aborted.IsCancellationRequested);
            Assert.Equal(!clientAbort, deadline.IsCancellationRequested);
            if (boundary == "BeforeIdentity") Assert.Equal(0, witness.Saves);
            else
            {
                Assert.True(witness.IdentityQueries > 0);
                Assert.True(witness.Saves > 0);
            }

            var database = services.GetRequiredService<AuthDbContext>();
            Assert.Empty(database.ChangeTracker.Entries());
            Assert.Null(database.Database.CurrentTransaction);
            Assert.Null(services.GetRequiredService<RegistrationIdentityCancellation>().Token);
            Assert.Equal(previous, await ReadEnrollmentAsync(test));

            // Outside the onboarding scope, Identity still uses the original RequestAborted.
            witness.Armed = false;
            if (!clientAbort)
            {
                witness.FallbackToken = aborted.Token;
                await services.GetRequiredService<UserManager<User>>().FindByEmailAsync(input.Email!);
                Assert.Equal(1, witness.FallbackReads);
                witness.FallbackToken = null;
            }

            // The explicit later request is the first permitted use of the same scoped service/context.
            Assert.Equal(RegistrationOutcome.Accepted,
                (await ApplyAsync(service, action, input, token, CancellationToken.None)).Outcome);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("Resend")]
    [InlineData("Exchange")]
    public async Task Cancellation_with_lost_commit_acknowledgement_leaves_only_a_complete_atomic_outcome(string action)
    {
        using var deadline = new CancellationTokenSource();
        using var aborted = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, aborted.Token);
        var witness = new CancellationWitness("CommitAcknowledgement", deadline, linked.Token);
        await using var test = await fixture.PrepareAsync(new ReadBoundary(witness), new SaveBoundary(witness), new CommitBoundary(witness));
        var input = Input();
        var token = await SeedForAsync(test, action, input);
        var accessor = test.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token };
        try
        {
            witness.Armed = true;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                test.Run(service => ApplyAsync(service, action, input, token, linked.Token)));
        }
        finally
        {
            accessor.HttpContext = null;
        }

        Assert.True(deadline.IsCancellationRequested);
        Assert.False(aborted.IsCancellationRequested);
        Assert.Equal(1, witness.Cancellations);
        Assert.Equal(1, witness.CommitAttempts);
        Assert.Equal(1, witness.Transactions);
        Assert.Equal(0, witness.CommandsAfterCancellation);
        await using var db = test.Read();
        var kind = Enum.Parse<OnboardingOperationKind>(action);
        var user = await db.Users.AsNoTracking().SingleAsync();
        var receipt = await db.OnboardingOperationReceipts.AsNoTracking().SingleAsync(row => row.Kind == kind);
        var challenge = await db.EmailVerificationChallenges.AsNoTracking().SingleAsync(row => row.Id == receipt.ChallengeId);
        Assert.Equal(user.Id, receipt.UserId);
        Assert.Equal(user.Id, challenge.UserId);
        Assert.Equal(challenge.Generation, receipt.Generation);
        Assert.Equal(action == "Register" ? 1 : 2, await db.OnboardingOperationReceipts.CountAsync());
        if (action == "Exchange")
        {
            var grant = await db.OnboardingGrants.AsNoTracking().SingleAsync();
            Assert.Equal(grant.Id, receipt.GrantId);
            Assert.Equal(user.Id, grant.UserId);
            Assert.Equal(challenge.Id, grant.ChallengeId);
            Assert.Null(receipt.OutboxId);
            Assert.True(user.EmailConfirmed);
            Assert.Equal(UserStatus.Active, user.Status);
            Assert.Equal(VerificationChallengeState.Consumed, challenge.Status);
            Assert.Equal(receipt.Id, challenge.Version);
            Assert.Equal(1, await db.EmailVerificationChallenges.CountAsync());
            Assert.Equal(1, await db.VerificationMailOutboxMessages.CountAsync());
            witness.Armed = false;
            var replay = await test.Run(service => service.ExchangeAsync(token!, CancellationToken.None));
            Assert.Equal(RegistrationOutcome.Invalid, replay.Outcome);
            Assert.Null(replay.Credential);
        }
        else
        {
            var mail = await db.VerificationMailOutboxMessages.AsNoTracking().SingleAsync(row => row.Id == receipt.OutboxId);
            Assert.Equal(receipt.Id, mail.OperationId);
            Assert.Equal(challenge.Id, mail.ChallengeId);
            Assert.Equal(challenge.Generation, mail.Generation);
            Assert.Null(receipt.GrantId);
            Assert.False(user.EmailConfirmed);
            Assert.Equal(UserStatus.PendingVerification, user.Status);
            Assert.Equal(VerificationChallengeState.Pending, challenge.Status);
            Assert.Equal(action == "Register" ? 1 : 2, await db.EmailVerificationChallenges.CountAsync());
            Assert.Equal(action == "Register" ? 1 : 2, await db.VerificationMailOutboxMessages.CountAsync());
            Assert.Equal(0, await db.OnboardingGrants.CountAsync());
            if (action == "Resend")
            {
                var superseded = await db.EmailVerificationChallenges.AsNoTracking().SingleAsync(row => row.Id != challenge.Id);
                Assert.Equal(VerificationChallengeState.Superseded, superseded.Status);
                Assert.Equal(receipt.Id, superseded.Version);
            }
        }
    }

    private static async Task<string?> SeedForAsync(RegistrationPostgreSqlFixture.RegistrationFixtureHost test,
        string action, RegistrationRequest input)
    {
        if (action == "Register") return null;
        Assert.Equal(RegistrationOutcome.Accepted,
            await test.Run(service => service.RegisterAsync(input, CancellationToken.None)));
        test.Clock.Advance(TimeSpan.FromMinutes(2));
        return action == "Exchange" ? await test.CurrentToken() : null;
    }

    private static async Task<VerificationExchange> ApplyAsync(IRegistrationService service, string action,
        RegistrationRequest input, string? token, CancellationToken cancellationToken)
        => action switch
        {
            "Register" => new() { Outcome = await service.RegisterAsync(input, cancellationToken) },
            "Resend" => new() { Outcome = await service.ResendAsync(input.Email!, cancellationToken) },
            "Exchange" => await service.ExchangeAsync(token!, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

    private static async Task<EnrollmentState> ReadEnrollmentAsync(RegistrationPostgreSqlFixture.RegistrationFixtureHost test)
    {
        await using var db = test.Read();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync();
        var challenge = await db.EmailVerificationChallenges.AsNoTracking().SingleOrDefaultAsync();
        var outbox = await db.VerificationMailOutboxMessages.AsNoTracking().SingleOrDefaultAsync();
        var receipt = await db.OnboardingOperationReceipts.AsNoTracking().SingleOrDefaultAsync();
        Assert.Empty(await db.OnboardingGrants.AsNoTracking().Select(row => row.Id).ToListAsync());
        return new(user?.Id, user?.Status, user?.EmailConfirmed, challenge?.Id, challenge?.Status,
            challenge?.Generation, challenge?.Version, outbox?.Id, outbox?.OperationId, receipt?.Id);
    }

    private sealed record EnrollmentState(Guid? User, UserStatus? Status, bool? Confirmed,
        Guid? Challenge, VerificationChallengeState? ChallengeStatus, int? Generation, Guid? Version,
        Guid? Outbox, Guid? OutboxOperation, Guid? Receipt);

    private sealed class CancellationWitness(string boundary, CancellationTokenSource source, CancellationToken token)
    {
        public bool Armed { get; set; }
        public CancellationToken? FallbackToken { get; set; }
        public int FallbackReads { get; set; }
        public int Transactions { get; set; }
        public int CommitAttempts { get; set; }
        public int CommandsAfterCancellation { get; set; }
        public int IdentityQueries { get; set; }
        public int Saves { get; set; }
        public int Cancellations { get; private set; }

        public void RequireOperationToken(CancellationToken actual) => Assert.Equal(token, actual);

        public bool CancelAt(string current)
        {
            if (!Armed || boundary != current || Cancellations != 0) return false;
            Cancellations++;
            source.Cancel();
            return true;
        }
    }

    private sealed class ReadBoundary(CancellationWitness witness) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (witness.Armed)
            {
                witness.RequireOperationToken(cancellationToken);
                if (cancellationToken.IsCancellationRequested) witness.CommandsAfterCancellation++;
                var where = command.CommandText.IndexOf("WHERE", StringComparison.Ordinal);
                if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && where >= 0
                    && command.CommandText.AsSpan(where).Contains("\"NormalizedUserName\"", StringComparison.Ordinal))
                    witness.IdentityQueries++;
            }
            else if (witness.FallbackToken is { } fallback)
            {
                Assert.Equal(fallback, cancellationToken);
                witness.FallbackReads++;
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            witness.CancelAt("BeforeIdentity");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SaveBoundary(CancellationWitness witness) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (witness.Armed)
            {
                witness.RequireOperationToken(cancellationToken);
                witness.Saves++;
                if (witness.Saves == 1) witness.CancelAt("AfterIdentitySave");
                if (eventData.Context!.ChangeTracker.Entries<OnboardingOperationReceipt>().Any())
                    witness.CancelAt("AfterFinalSave");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitBoundary(CancellationWitness witness) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection,
            TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            if (witness.Armed)
            {
                witness.RequireOperationToken(cancellationToken);
                witness.Transactions++;
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (witness.Armed)
            {
                witness.RequireOperationToken(cancellationToken);
                witness.CommitAttempts++;
            }
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (witness.CancelAt("CommitAcknowledgement"))
                throw new NpgsqlException("Synthetic acknowledgement fault at the cancellation boundary", new TimeoutException());
            return Task.CompletedTask;
        }
    }

    private sealed class FailAfterInsertBatch : DbCommandInterceptor
    {
        public int Executions { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("INSERT INTO \"OnboardingOperationReceipts\"", StringComparison.Ordinal))
                return result;
            Executions++;
            await result.DisposeAsync();
            throw new DbUpdateException("Synthetic provider result rejected after the enrollment batch returned");
        }
    }

    private static RegistrationRequest Input(string? email = null) => new()
    {
        Email = email ?? $"fixture-{Guid.NewGuid():N}@example.invalid",
        FirstName = "Ada", LastName = "Witness", Password = $"Aa7!{Guid.NewGuid():N}"
    };

    private sealed class CommitFault(bool afterCommit, bool armed = true) : DbTransactionInterceptor
    {
        public bool Armed { get; set; } = armed;
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) MaybeThrow();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (afterCommit) MaybeThrow();
            return Task.CompletedTask;
        }

        private void MaybeThrow()
        {
            if (!Armed) return;
            Armed = false;
            Failures++;
            throw new NpgsqlException("Synthetic acknowledgement fault", new TimeoutException());
        }
    }

    private sealed class ReceiptFault : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("\"OnboardingOperationReceipts\"", StringComparison.Ordinal))
            {
                Armed = false;
                Failures++;
                throw new NpgsqlException("Synthetic receipt read fault", new TimeoutException());
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailChallengeWrite : SaveChangesInterceptor
    {
        public bool SawIdentitySave { get; private set; }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (result == 1) SawIdentitySave = eventData.Context?.Database.CurrentTransaction is not null;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<EmailVerificationChallenge>().Any())
                throw new DbUpdateException("Synthetic verification write rejection");
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class RegistrationPostgreSqlFixture : IAsyncLifetime
{
    private const string Image = "postgres@sha256:5c855ad7b85e68e48a62f34662853f38b57c1c1d80f3a927ab58034fd6d31c5e";
    private readonly string adminCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string runtimeCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private IContainer database = null!;
    private string keyPath = null!;

    public async Task InitializeAsync()
    {
        keyPath = Environment.GetEnvironmentVariable("ZEKA_ZEK27_VERIFICATION_KEY_FILE")
            ?? throw new InvalidOperationException("ZEK27_PROTECTED_VERIFICATION_KEY_FILE_REQUIRED");
        if (!Path.IsPathFullyQualified(keyPath) || !File.Exists(keyPath))
            throw new InvalidOperationException("ZEK27_VERIFICATION_KEY_PATH_REJECTED");

        var identity = Guid.NewGuid().ToString("N");
        database = new ContainerBuilder(Image)
            .WithName("zeka-zek27-registration-pg-" + identity)
            .WithLabel("com.zeka.fixture", "zek27-registration")
            .WithEnvironment("POSTGRES_PASSWORD_FILE", "/tmp/zeka-fixture-db-secret")
            .WithResourceMapping(Encoding.UTF8.GetBytes(adminCredential),
                FilePath.Of("/tmp/zeka-fixture-db-secret"), 0, 0, UnixFileModes.UserRead)
            .WithPortBinding(5432, true)
            .WithCreateParameterModifier(create =>
            {
                foreach (var binding in (create.HostConfig?.PortBindings ?? throw new InvalidOperationException("ZEK27_PORT_BINDING_ABSENT"))
                    .Values.SelectMany(entries => entries))
                    binding.HostIP = "127.0.0.1";
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "postgres"))
            .WithAutoRemove(true)
            .WithCleanUp(false)
            .Build();
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        await database.StartAsync(startup.Token);
        await using var connection = new NpgsqlConnection(Connection("postgres", runtime: false));
        await connection.OpenAsync(startup.Token);
        await using var version = new NpgsqlCommand("SHOW server_version_num", connection);
        Assert.StartsWith("17", (string)(await version.ExecuteScalarAsync(startup.Token))!);
    }

    public async Task DisposeAsync()
    {
        if (database is not null) await database.DisposeAsync();
    }

    internal async Task<string> CreateEmptyDatabaseRuntimeAsync()
        => Connection(await CreateDatabaseAsync(), runtime: true);

    private async Task<string> CreateDatabaseAsync()
    {
        var name = "zek27_" + Guid.NewGuid().ToString("N");
        await using (var root = new NpgsqlConnection(Connection("postgres", runtime: false)))
        {
            await root.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", root).ExecuteNonQueryAsync();
        }
        return name;
    }

    internal async Task<RegistrationFixtureHost> PrepareAsync(params IInterceptor[] interceptors)
    {
        var name = await CreateDatabaseAsync();
        var owner = Connection(name, runtime: false);
        var runtime = Connection(name, runtime: true);
        await BootstrapAsync(owner);
        await using (var db = new AuthDbContext(DbOptions(owner)))
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await db.Database.MigrateAsync();
        }
        await BootstrapAsync(owner);
        await BootstrapAsync(owner);
        await VerifyRuntimePermissionsAsync(runtime);

        var clock = new FixtureClock();
        var builder = CreateBuilder(runtime, clock, interceptors);
        return new(builder.Services.BuildServiceProvider(), clock, runtime);
    }

    internal WebApplicationBuilder CreateBuilder(string runtime, FixtureClock clock, params IInterceptor[] interceptors)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "LocalStack" });
        builder.Logging.ClearProviders();
        builder.Configuration["OnboardingRegistration:Enabled"] = "true";
        builder.Configuration["OnboardingRegistration:Deployment"] = "zek27-fixture";
        builder.Configuration["OnboardingRegistration:ActiveKeyId"] = "fixture-v1";
        builder.Configuration["OnboardingRegistration:VerificationKeyFiles:fixture-v1"] = keyPath;
        builder.Services.AddDbContext<AuthDbContext>(options => options.UseNpgsql(runtime,
            pg => pg.EnableRetryOnFailure(2, TimeSpan.FromSeconds(1), null).CommandTimeout(6))
            .AddInterceptors(interceptors));
        builder.ConfigureMicrosoftIdentity();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddVerificationRegistration(builder.Configuration, builder.Environment);
        return builder;
    }

    private static DbContextOptions<AuthDbContext> DbOptions(string connection)
        => new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection, pg => pg.CommandTimeout(12)).Options;

    private async Task BootstrapAsync(string owner)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        const string relative = "Deployments/database/bootstrap-auth-roles.sql";
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
            directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("ZEK27_AUTH_BOOTSTRAP_SOURCE_NOT_FOUND");
        var bootstrap = await File.ReadAllTextAsync(Path.Combine(directory.FullName, relative));

        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await new NpgsqlCommand(bootstrap, connection).ExecuteNonQueryAsync();
        await using (var parameter = new NpgsqlCommand("SELECT set_config('zeka.fixture_runtime', @value, false)", connection))
        {
            parameter.Parameters.AddWithValue("value", runtimeCredential);
            await parameter.ExecuteNonQueryAsync();
        }
        await new NpgsqlCommand("""
            DO $fixture$
            BEGIN
                EXECUTE format('ALTER ROLE zeka_auth_runtime PASSWORD %L', current_setting('zeka.fixture_runtime'));
            END $fixture$;
            """, connection).ExecuteNonQueryAsync();
    }

    private static async Task VerifyRuntimePermissionsAsync(string runtime)
    {
        await using var connection = new NpgsqlConnection(runtime);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_user = 'zeka_auth_runtime' AND NOT role.rolsuper AND NOT role.rolbypassrls
                AND has_table_privilege(current_user, 'public."EmailVerificationChallenges"', 'SELECT')
                AND has_table_privilege(current_user, 'public."EmailVerificationChallenges"', 'INSERT')
                AND has_column_privilege(current_user, 'public."EmailVerificationChallenges"', 'Status', 'UPDATE')
                AND NOT has_column_privilege(current_user, 'public."EmailVerificationChallenges"', 'TokenHash', 'UPDATE')
                AND NOT has_table_privilege(current_user, 'public."EmailVerificationChallenges"', 'DELETE')
                AND has_table_privilege(current_user, 'public."VerificationMailOutboxMessages"', 'INSERT')
                AND has_column_privilege(current_user, 'public."VerificationMailOutboxMessages"', 'LeaseId', 'UPDATE')
                AND NOT has_column_privilege(current_user, 'public."VerificationMailOutboxMessages"', 'ChallengeId', 'UPDATE')
                AND has_table_privilege(current_user, 'public."OnboardingGrants"', 'INSERT')
                AND NOT has_table_privilege(current_user, 'public."OnboardingGrants"', 'UPDATE')
                AND has_table_privilege(current_user, 'public."OnboardingOperationReceipts"', 'SELECT')
                AND NOT has_table_privilege(current_user, 'public."OnboardingOperationReceipts"', 'UPDATE')
            FROM pg_catalog.pg_roles AS role WHERE role.rolname = current_user;
            """, connection);
        Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private string Connection(string name, bool runtime) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = database.GetMappedPublicPort(5432), Database = name,
        Username = runtime ? "zeka_auth_runtime" : "postgres",
        Password = runtime ? runtimeCredential : adminCredential,
        Timeout = 5, CommandTimeout = 20, Pooling = false
    }.ConnectionString;

    internal sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }

    internal sealed class RegistrationFixtureHost(ServiceProvider services, FixtureClock clock, string runtime) : IAsyncDisposable
    {
        public FixtureClock Clock => clock;
        public IServiceProvider Services => services;
        public string Runtime => runtime;
        public AuthDbContext Read() => new(DbOptions(runtime));

        public async Task<T> Run<T>(Func<RegistrationService, Task<T>> operation)
        {
            await using var scope = services.CreateAsyncScope();
            return await operation((RegistrationService)scope.ServiceProvider.GetRequiredService<IRegistrationService>());
        }

        public async Task<string> CurrentToken()
        {
            await using var db = Read();
            var row = await db.EmailVerificationChallenges.AsNoTracking()
                .SingleAsync(item => item.Status == VerificationChallengeState.Pending);
            return services.GetRequiredService<VerificationTokenService>().Create(row)!;
        }

        public ValueTask DisposeAsync() => services.DisposeAsync();
    }
}
