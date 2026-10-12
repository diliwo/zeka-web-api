using System.Data.Common;
using System.Diagnostics;
using AuthManager.Application.Authorization;
using AuthManager.Core.Enums;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "72")]
[Collection(PostgreSqlCollection.CollectionName)]
public sealed class AssigneeEligibilityResolverTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Production_retry_strategy_reads_current_authority_inside_its_transaction()
    {
        await using var state = await State.CreateAsync(postgres, enableRetries: true);
        Assert.Equal("Assigned", (await state.ResolveAsync())?.TargetViewScope);

        state.TargetMembership.Suspend(DateTimeOffset.UtcNow.AddSeconds(1));
        await state.Database.SaveChangesAsync();
        Assert.Null(await state.ResolveAsync());
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Transient_authority_failure_replays_a_fresh_observation_at_most_once(
        int transientFailures, bool shouldSucceed)
    {
        var failures = new SyntheticTransientFailure(transientFailures);
        await using var state = await State.CreateAsync(postgres, enableRetries: true, failures);
        failures.Arm();

        if (shouldSucceed)
            Assert.Equal("Assigned", (await state.ResolveAsync())?.TargetViewScope);
        else
            await Assert.ThrowsAsync<InvalidOperationException>(state.ResolveAsync);

        Assert.Equal(transientFailures, failures.Faults);
    }

    [Fact]
    public async Task Retry_enabled_observation_honours_one_total_two_second_deadline()
    {
        var delayedRead = new DelayedAuthorityRead();
        await using var state = await State.CreateAsync(postgres, enableRetries: true, delayedRead);
        delayedRead.Arm();

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(state.ResolveAsync);
        watch.Stop();
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3.0);
        Assert.Equal(1, delayedRead.Attempts);
    }

    [Fact]
    public async Task Transient_retry_and_second_attempt_share_the_total_deadline()
    {
        var failures = new SyntheticTransientFailure(1, delayAfterFailures: true);
        await using var state = await State.CreateAsync(postgres, enableRetries: true, failures);
        failures.Arm();

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(state.ResolveAsync);
        watch.Stop();
        Assert.InRange(watch.Elapsed.TotalSeconds, 1.8, 3.0);
        Assert.Equal(1, failures.Faults);
        Assert.Equal(2, failures.Reads);
    }

    [Fact]
    public async Task Revocation_between_retry_attempts_is_observed_not_reused()
    {
        var revoke = new RevokeOnTransientGrantRead();
        await using var state = await State.CreateAsync(postgres, enableRetries: true, revoke);
        revoke.Arm(state.TargetMembership.Id, state.Database.Database.GetConnectionString()!);

        Assert.Null(await state.ResolveAsync());
        Assert.Equal(1, revoke.Faults);
    }

    [Fact]
    public async Task Live_authority_changes_view_scope_and_revocation_denies_without_a_positive_cache()
    {
        await using var state = await State.CreateAsync(postgres);
        var first = await state.ResolveAsync();
        Assert.Equal("Assigned", first?.TargetViewScope);
        Assert.Equal(state.ObservedOrganisationId, state.Request.OrganisationId);
        Assert.True(first?.ObservedAtUtc <= DateTimeOffset.UtcNow);

        state.Database.Entry(state.TargetMembership).Property(x => x.PermissionSetId).CurrentValue = PermissionSet.ViewerId;
        await state.Database.SaveChangesAsync();
        var allScope = await state.ResolveAsync();
        Assert.Equal("All", allScope?.TargetViewScope);
        Assert.NotEqual(first!.DecisionVersion, allScope!.DecisionVersion);

        state.TargetMembership.Suspend(DateTimeOffset.UtcNow.AddSeconds(1));
        await state.Database.SaveChangesAsync();
        Assert.Null(await state.ResolveAsync());
        Assert.Null(await state.ResolveAsync());
    }

    [Theory]
    [InlineData("actor_suspended")]
    [InlineData("actor_left")]
    [InlineData("actor_no_edit")]
    [InlineData("actor_subject_mismatch")]
    [InlineData("target_suspended")]
    [InlineData("target_left")]
    [InlineData("target_missing")]
    [InlineData("target_other_organisation")]
    [InlineData("target_unknown_permission_set")]
    [InlineData("organisation_suspended")]
    [InlineData("target_user_suspended")]
    [InlineData("invalid_explicit_grant")]
    public async Task Two_organisation_denials_are_uniform_and_current(string condition)
    {
        await using var state = await State.CreateAsync(postgres);
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        switch (condition)
        {
            case "actor_suspended": state.ActorMembership.Suspend(now); break;
            case "actor_left": state.ActorMembership.Leave(now); break;
            case "actor_no_edit":
                state.Database.Entry(state.ActorMembership).Property(x => x.PermissionSetId).CurrentValue = PermissionSet.LimitedViewerId;
                break;
            case "actor_subject_mismatch": state.Request = state.Request with { ActorSubjectId = Guid.NewGuid() }; break;
            case "target_suspended": state.TargetMembership.Suspend(now); break;
            case "target_left": state.TargetMembership.Leave(now); break;
            case "target_missing": state.Request = state.Request with { TargetMembershipId = Guid.NewGuid() }; break;
            case "target_other_organisation":
                state.Request = state.Request with { TargetMembershipId = state.OtherOrganisationMembership.Id }; break;
            case "target_unknown_permission_set":
                var unknownId = Guid.NewGuid();
                await state.Database.Database.ExecuteSqlInterpolatedAsync(
                    $"insert into \"PermissionSets\" (\"Id\", \"Code\", \"DisplayName\", \"IsSystem\") values ({unknownId}, {'B'} || 'roken', 'Synthetic malformed', true)");
                state.Database.Entry(state.TargetMembership).Property(x => x.PermissionSetId).CurrentValue = unknownId;
                break;
            case "organisation_suspended": state.Organisation.Suspend(now); break;
            case "target_user_suspended":
                state.Database.Entry(state.TargetUser).Property(x => x.Status).CurrentValue = UserStatus.Suspended;
                break;
            case "invalid_explicit_grant":
                state.Database.Add(MembershipPermissionGrant.Create(Guid.NewGuid(), state.Organisation.Id,
                    state.TargetMembership.Id, "Clients.ViewAll", state.ActorMembership.Id,
                    state.ActorUser.Id, now));
                break;
        }
        await state.Database.SaveChangesAsync();
        Assert.Null(await state.ResolveAsync());
        Assert.Null(await state.ResolveAsync());
    }

    [Fact]
    public async Task Database_rejects_duplicate_membership_for_one_subject_and_organisation()
    {
        await using var state = await State.CreateAsync(postgres);
        state.Database.Add(OrganisationMembership.Create(Guid.NewGuid(), state.Organisation.Id,
            state.TargetUser.Id, PermissionSet.LimitedViewerId, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => state.Database.SaveChangesAsync());
    }

    [Fact]
    public async Task Corrupt_duplicate_actor_or_target_linkage_denies_before_joined_permission_resolution()
    {
        await using var state = await State.CreateAsync(postgres);
        // Test-only corruption of an isolated synthetic database; production's unique index remains unchanged.
        await state.Database.Database.ExecuteSqlRawAsync(
            "drop index \"IX_OrganisationMemberships_UserId_OrganisationId\"");
        await state.Database.Database.ExecuteSqlRawAsync(
            "alter table \"OrganisationMemberships\" drop constraint \"FK_OrganisationMemberships_PermissionSets_PermissionSetId\"");
        var duplicateActor = OrganisationMembership.Create(Guid.NewGuid(), state.Organisation.Id,
            state.ActorUser.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        state.Database.Add(duplicateActor);
        await state.Database.SaveChangesAsync();
        Assert.Null(await state.ResolveAsync());

        state.Database.Remove(duplicateActor);
        await state.Database.SaveChangesAsync();

        state.Database.Add(OrganisationMembership.Create(Guid.NewGuid(), state.Organisation.Id,
            state.TargetUser.Id, PermissionSet.LimitedViewerId, DateTimeOffset.UtcNow));
        await state.Database.SaveChangesAsync();
        Assert.Null(await state.ResolveAsync());
    }

    private sealed class State : IAsyncDisposable
    {
        public AuthDbContext Database { get; }
        public Organisation Organisation { get; }
        public User ActorUser { get; }
        public User TargetUser { get; }
        public OrganisationMembership ActorMembership { get; }
        public OrganisationMembership TargetMembership { get; }
        public OrganisationMembership OtherOrganisationMembership { get; }
        public Guid ObservedOrganisationId => Organisation.Id;
        public AssigneeEligibilityRequest Request { get; set; }

        private State(AuthDbContext database, Organisation organisation, User actorUser, User targetUser,
            OrganisationMembership actorMembership, OrganisationMembership targetMembership,
            OrganisationMembership otherMembership)
        {
            Database = database; Organisation = organisation; ActorUser = actorUser; TargetUser = targetUser;
            ActorMembership = actorMembership; TargetMembership = targetMembership;
            OtherOrganisationMembership = otherMembership;
            Request = new(1, organisation.Id, actorUser.Id, actorMembership.Id, targetMembership.Id);
        }

        public static async Task<State> CreateAsync(PostgreSqlFixture postgres, bool enableRetries = false,
            DbCommandInterceptor? interceptor = null)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var builder = new DbContextOptionsBuilder<AuthDbContext>()
                .UseNpgsql(connection, npgsql =>
                {
                    if (enableRetries)
                        npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(40), ["0"]);
                });
            if (interceptor is not null) builder.AddInterceptors(interceptor);
            var options = builder.Options;
            var database = new AuthDbContext(options);
            await database.Database.EnsureCreatedAsync();
            var now = DateTimeOffset.UtcNow;
            var actor = User.Create($"actor-{Guid.NewGuid():N}@example.invalid", $"actor-{Guid.NewGuid():N}", "Synthetic", "Actor", now);
            var target = User.Create($"target-{Guid.NewGuid():N}@example.invalid", $"target-{Guid.NewGuid():N}", "Synthetic", "Target", now);
            var other = User.Create($"other-{Guid.NewGuid():N}@example.invalid", $"other-{Guid.NewGuid():N}", "Synthetic", "Other", now);
            foreach (var user in new[] { actor, target, other })
            {
                user.EmailConfirmed = true;
                database.Add(user);
                database.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Active;
            }
            var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic A", actor.Id, now);
            var otherOrganisation = Organisation.Create(Guid.NewGuid(), "Synthetic B", other.Id, now);
            organisation.Activate(now); otherOrganisation.Activate(now);
            var actorMembership = OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, actor.Id, now);
            var targetMembership = OrganisationMembership.Create(Guid.NewGuid(), organisation.Id, target.Id,
                PermissionSet.LimitedViewerId, now);
            var otherMembership = OrganisationMembership.CreateOwner(Guid.NewGuid(), otherOrganisation.Id, other.Id, now);
            database.AddRange(organisation, otherOrganisation, actorMembership, targetMembership, otherMembership);
            await database.SaveChangesAsync();
            return new State(database, organisation, actor, target, actorMembership, targetMembership, otherMembership);
        }

        public Task<AssigneeEligibilityDecision?> ResolveAsync() =>
            new AssigneeEligibilityResolver(Database, TimeProvider.System).ResolveAsync(Request, CancellationToken.None);

        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }

    private sealed class SyntheticTransientFailure(int failuresToInject, bool delayAfterFailures = false) : DbCommandInterceptor
    {
        private bool armed;
        public int Faults { get; private set; }
        public int Reads { get; private set; }
        public void Arm() => armed = true;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (armed && command.CommandText.Contains("Organisations", StringComparison.Ordinal))
            {
                Reads++;
                if (Faults < failuresToInject)
                {
                    Faults++;
                    throw new NpgsqlException("Synthetic transient authority read failure", new TimeoutException());
                }
                if (delayAfterFailures)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class DelayedAuthorityRead : DbCommandInterceptor
    {
        private bool armed;
        public int Attempts { get; private set; }
        public void Arm() => armed = true;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (armed && command.CommandText.Contains("Organisations", StringComparison.Ordinal))
            {
                Attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class RevokeOnTransientGrantRead : DbCommandInterceptor
    {
        private Guid targetMembershipId;
        private string connectionString = "";
        public int Faults { get; private set; }
        public void Arm(Guid targetId, string databaseConnectionString)
        { targetMembershipId = targetId; connectionString = databaseConnectionString; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (targetMembershipId != Guid.Empty && Faults == 0
                && command.CommandText.Contains("MembershipPermissionGrants", StringComparison.Ordinal))
            {
                // A separate authoritative write commits after attempt one's membership reads.
                await using var writer = new NpgsqlConnection(connectionString);
                await writer.OpenAsync(cancellationToken);
                await using var revoke = new NpgsqlCommand(
                    "delete from \"OrganisationMemberships\" where \"Id\" = @id", writer);
                revoke.Parameters.AddWithValue("id", targetMembershipId);
                Assert.Equal(1, await revoke.ExecuteNonQueryAsync(cancellationToken));
                Faults++;
                throw new NpgsqlException("Synthetic transient grant read failure", new TimeoutException());
            }
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
