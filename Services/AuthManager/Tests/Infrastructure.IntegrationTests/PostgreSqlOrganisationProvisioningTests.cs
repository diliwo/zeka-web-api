using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Provisioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "ZEK-17")]
[Trait("Evidence", "ApplicationConformance")]
public sealed class PostgreSqlOrganisationProvisioningTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Runtime_commit_and_fresh_context_replay_preserve_one_owner_effect_and_transaction_local_tenant()
    {
        var runtime = await CreateRuntimeAsync();
        var subject = Guid.NewGuid();
        var command = new OrganisationProvisioningCommand("Provider owner organisation", null, "provider-stable-request", null);
        OrganisationProvisioningResult created;
        await using (var context = new RuntimeContext(runtime))
        {
            var database = context.Database;
            await database.Database.OpenConnectionAsync();
            await AssertRestrictedRuntimeAsync(database);
            Assert.True(string.IsNullOrEmpty(await TenantAsync(database)));
            var identity = new FixtureIdentity(database, subject);
            var repository = new ObservedRepository(database);

            created = await context.Service(identity, repository).ProvisionAsync(command);

            Assert.Equal(OrganisationProvisioningOutcome.Created, created.Outcome);
            Assert.Equal(1, identity.EnsureCalls);
            Assert.Equal(created.OrganisationId.ToString("D"), repository.ObservedTenant);
            Assert.True(string.IsNullOrEmpty(await TenantAsync(database)));
            Assert.Null(database.Database.CurrentTransaction);
        }

        await using var replayContext = new RuntimeContext(runtime);
        var replayDatabase = replayContext.Database;
        await replayDatabase.Database.OpenConnectionAsync();
        await AssertRestrictedRuntimeAsync(replayDatabase);
        var replayIdentity = new FixtureIdentity(replayDatabase, subject);
        var replayRepository = new ObservedRepository(replayDatabase);
        var replay = await replayContext.Service(replayIdentity, replayRepository).ProvisionAsync(command);

        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created.SubjectId, replay.SubjectId);
        Assert.Equal(created.OrganisationId, replay.OrganisationId);
        Assert.Equal(created.MembershipId, replay.MembershipId);
        Assert.Equal(0, replayIdentity.EnsureCalls);
        Assert.Null(replayRepository.ObservedTenant);
        Assert.True(string.IsNullOrEmpty(await TenantAsync(replayDatabase)));
        await AssertCountsAsync(replayDatabase, 1);
        var organisation = await replayDatabase.Organisations.AsNoTracking().SingleAsync();
        var membership = await replayDatabase.OrganisationMemberships.AsNoTracking().SingleAsync();
        Assert.Equal(subject, organisation.OwnerUserId);
        Assert.Equal(OrganisationStatus.Active, organisation.Status);
        Assert.Null(organisation.TimeZoneId);
        Assert.Equal(0, organisation.TimeZoneConfigVersion);
        Assert.Equal(created.MembershipId, membership.Id);
        Assert.Equal(created.OrganisationId, membership.OrganisationId);
        Assert.Equal(subject, membership.UserId);
        Assert.Equal(PermissionSet.OrganisationOwnerId, membership.PermissionSetId);
        Assert.Equal(MembershipStatus.Active, membership.Status);
        Assert.Contains("Organisations.Close", TenantPermissions.Resolve(membership.PermissionSetId, "Owner")!);
        var audit = await replayDatabase.AuditEntries.AsNoTracking().SingleAsync();
        Assert.Equal("organisation.provisioned", audit.Action);
        Assert.Equal("Organisation", audit.SubjectType);
        using var metadata = JsonDocument.Parse(audit.Metadata);
        Assert.Equal(OrganisationProvisioningAudit.OperatorNoAgreements,
            metadata.RootElement.GetProperty("agreementStatus").GetString());
        Assert.Equal(0, await replayDatabase.AuditEntries.CountAsync(value =>
            value.Action == "agreement.accepted" || value.SubjectType == "AgreementAcceptance"));
        var outbox = await replayDatabase.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.Equal(OrganisationProvisioningEvents.MessageType, outbox.MessageType);
        Assert.Equal(1, outbox.ContractVersion);
        Assert.Equal(created.OrganisationId, outbox.OrganisationId);
        Assert.Equal(audit.CorrelationId, outbox.CorrelationId);
        using var payload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(3, payload.RootElement.EnumerateObject().Count());
        Assert.Equal(created.OrganisationId, payload.RootElement.GetProperty("OrganisationId").GetGuid());
        Assert.Equal(subject, payload.RootElement.GetProperty("OwnerUserId").GetGuid());
        Assert.Equal(created.MembershipId, payload.RootElement.GetProperty("OwnerMembershipId").GetGuid());
        Assert.Equal(0, await replayDatabase.BrowserSessionFamilies.CountAsync());
    }

    [Fact]
    public async Task Runtime_failure_after_persisted_outbox_rolls_back_every_provisioning_row_and_tenant_context()
    {
        var runtime = await CreateRuntimeAsync();
        await using var context = new RuntimeContext(runtime);
        var database = context.Database;
        await database.Database.OpenConnectionAsync();
        await AssertRestrictedRuntimeAsync(database);
        var subject = Guid.NewGuid();
        var command = new OrganisationProvisioningCommand("Provider rollback", null, "provider-rollback-request", null);
        var identity = new FixtureIdentity(database, subject);
        var repository = new ObservedRepository(database);
        var outbox = new RefuseAfterOutboxSave(context.Outbox);

        var failure = await Assert.ThrowsAsync<OrganisationProvisioningPersistenceException>(() =>
            context.Service(identity, repository, outbox).ProvisionAsync(command));

        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", failure.Code);
        Assert.False(failure.RequiresInspection);
        Assert.Equal(1, identity.EnsureCalls);
        Assert.Equal(1, outbox.PersistedWrites);
        Assert.NotNull(repository.ObservedTenant);
        Assert.Empty(database.ChangeTracker.Entries());
        Assert.Null(database.Database.CurrentTransaction);
        Assert.True(string.IsNullOrEmpty(await TenantAsync(database)));
        // A separate context proves durable absence, rather than tracked-state rollback only.
        await using var verification = new AuthDbContext(Options(runtime));
        await AssertCountsAsync(verification, 0);

        // The same durable intent can start afresh after a fully rolled-back first use.
        await using var recovery = new RuntimeContext(runtime);
        var recovered = await recovery.Service(new FixtureIdentity(recovery.Database, subject),
            new ObservedRepository(recovery.Database)).ProvisionAsync(command);
        Assert.Equal(OrganisationProvisioningOutcome.Created, recovered.Outcome);
        Assert.Equal(subject, recovered.SubjectId);
        await using var recoveredVerification = new AuthDbContext(Options(runtime));
        await AssertCountsAsync(recoveredVerification, 1);
        Assert.Equal(recovered.OrganisationId, (await recoveredVerification.Organisations.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(recovered.MembershipId, (await recoveredVerification.OrganisationMemberships.AsNoTracking().SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_first_use_transactions_commit_once_then_recover_by_replay_or_conflict(bool conflictingIntent)
    {
        var runtime = await CreateRuntimeAsync();
        var subject = Guid.NewGuid();
        var command = new OrganisationProvisioningCommand("Concurrent first owner", null, "concurrent-first-use", null);
        var followerCommand = conflictingIntent ? command with { DisplayName = "Conflicting organisation intent" } : command;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var race = new FirstUseRace();
        await using var leader = new RuntimeContext(runtime, new ClaimCommandObserver(race, leader: true));
        await using var follower = new RuntimeContext(runtime, new ClaimCommandObserver(race, leader: false));
        await leader.Database.Database.OpenConnectionAsync(deadline.Token);
        await follower.Database.Database.OpenConnectionAsync(deadline.Token);
        await AssertRestrictedRuntimeAsync(leader.Database);
        await AssertRestrictedRuntimeAsync(follower.Database);
        Assert.NotEqual(((NpgsqlConnection)leader.Database.Database.GetDbConnection()).ProcessID,
            ((NpgsqlConnection)follower.Database.Database.GetDbConnection()).ProcessID);
        var leaderIdentity = new FixtureIdentity(leader.Database, subject);
        var followerIdentity = new FixtureIdentity(follower.Database, subject);
        var leaderRepository = new ObservedRepository(leader.Database);
        var followerRepository = new ObservedRepository(follower.Database);

        async Task<ProvisioningAttempt> RunLeaderAsync()
        {
            try { return await CaptureAttemptAsync(leader.Service(leaderIdentity, leaderRepository), command, deadline.Token); }
            finally { race.LeaderFinished.TrySetResult(true); }
        }

        // Both real SERIALIZABLE transactions first observe an absent claim. The follower
        // keeps that snapshot while the leader commits, then attempts its claim. This
        // deterministically exercises stale-snapshot contention, not DB-observed lock waiting.
        var leaderTask = RunLeaderAsync();
        var followerTask = CaptureAttemptAsync(follower.Service(followerIdentity, followerRepository), followerCommand, deadline.Token);
        var attempts = await Task.WhenAll(leaderTask, followerTask);
        Assert.Null(attempts[0].Failure);
        var created = Assert.IsType<OrganisationProvisioningResult>(attempts[0].Result);
        Assert.Equal(OrganisationProvisioningOutcome.Created, created.Outcome);
        Assert.Null(attempts[1].Result);
        var refusal = Assert.IsType<OrganisationProvisioningPersistenceException>(attempts[1].Failure);
        Assert.Equal("PROVISIONING_TRANSACTION_REJECTED", refusal.Code);
        Assert.False(refusal.RequiresInspection);
        Assert.Equal(2, race.EmptyFirstReads);
        var nativeState = Assert.Single(race.ClaimInsertFailureStates);
        Assert.True(nativeState is "40001" or "23505", "Expected PostgreSQL serialization or competing unique-claim refusal.");
        Assert.Equal(1, leaderIdentity.EnsureCalls);
        Assert.Equal(0, followerIdentity.EnsureCalls);
        Assert.Null(followerRepository.ObservedTenant);
        Assert.Empty(follower.Database.ChangeTracker.Entries());
        Assert.Null(leader.Database.Database.CurrentTransaction);
        Assert.Null(follower.Database.Database.CurrentTransaction);
        Assert.True(string.IsNullOrEmpty(await TenantAsync(leader.Database)));
        Assert.True(string.IsNullOrEmpty(await TenantAsync(follower.Database)));

        await using var replayContext = new RuntimeContext(runtime);
        var before = await replayContext.Database.IdempotencyRecords.AsNoTracking().SingleAsync(deadline.Token);
        var replayIdentity = new FixtureIdentity(replayContext.Database, subject);
        var replay = await replayContext.Service(replayIdentity, new ObservedRepository(replayContext.Database))
            .ProvisionAsync(command, deadline.Token);
        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(created.SubjectId, replay.SubjectId);
        Assert.Equal(created.OrganisationId, replay.OrganisationId);
        Assert.Equal(created.MembershipId, replay.MembershipId);
        Assert.Equal(0, replayIdentity.EnsureCalls);

        if (conflictingIntent)
        {
            await using var conflictContext = new RuntimeContext(runtime);
            var conflictIdentity = new FixtureIdentity(conflictContext.Database, subject);
            var conflict = await conflictContext.Service(conflictIdentity, new ObservedRepository(conflictContext.Database))
                .ProvisionAsync(followerCommand, deadline.Token);
            Assert.Equal(OrganisationProvisioningOutcome.Conflict, conflict.Outcome);
            Assert.Equal(subject, conflict.SubjectId);
            Assert.Equal(Guid.Empty, conflict.OrganisationId);
            Assert.Equal(Guid.Empty, conflict.MembershipId);
            Assert.Equal(0, conflictIdentity.EnsureCalls);
        }

        await using var verification = new AuthDbContext(Options(runtime));
        await AssertCountsAsync(verification, 1);
        var organisation = await verification.Organisations.AsNoTracking().SingleAsync(deadline.Token);
        Assert.Equal(created.OrganisationId, organisation.Id);
        Assert.Equal(command.DisplayName, organisation.DisplayName);
        Assert.Equal(subject, organisation.OwnerUserId);
        var membership = await verification.OrganisationMemberships.AsNoTracking().SingleAsync(deadline.Token);
        Assert.Equal(created.MembershipId, membership.Id);
        Assert.Equal(created.OrganisationId, membership.OrganisationId);
        Assert.Equal(subject, membership.UserId);
        Assert.Equal(PermissionSet.OrganisationOwnerId, membership.PermissionSetId);
        var after = await verification.IdempotencyRecords.AsNoTracking().SingleAsync(deadline.Token);
        Assert.Equal(before.RequestHash, after.RequestHash);
        Assert.Equal(before.SafeResponse, after.SafeResponse);
        Assert.Equal(before.ResourceId, after.ResourceId);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(created.OrganisationId, after.ResourceId);
    }

    private sealed record ProvisioningAttempt(OrganisationProvisioningResult? Result,
        OrganisationProvisioningPersistenceException? Failure);

    private static async Task<ProvisioningAttempt> CaptureAttemptAsync(OrganisationProvisioningService service,
        OrganisationProvisioningCommand command, CancellationToken cancellationToken)
    {
        try { return new(await service.ProvisionAsync(command, cancellationToken), null); }
        catch (OrganisationProvisioningPersistenceException failure) { return new(null, failure); }
    }

    private sealed class FirstUseRace
    {
        private int emptyFirstReads;
        public int EmptyFirstReads => Volatile.Read(ref emptyFirstReads);
        public TaskCompletionSource<bool> BothReadAbsent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> LeaderFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<string> ClaimInsertFailureStates { get; } = new();

        public async Task ObserveAsync(bool leader, DbDataReader result, CancellationToken cancellationToken)
        {
            Assert.False(result.HasRows);
            var count = Interlocked.Increment(ref emptyFirstReads);
            Assert.InRange(count, 1, 2);
            if (count == 2) BothReadAbsent.TrySetResult(true);
            await BothReadAbsent.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!leader) await LeaderFinished.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ClaimCommandObserver(FirstUseRace race, bool leader) : DbCommandInterceptor
    {
        private int firstRead;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"IdempotencyRecords\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref firstRead, 1, 0) == 0)
                await race.ObserveAsync(leader, result, cancellationToken);
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("\"IdempotencyRecords\"", StringComparison.Ordinal)
                && eventData.Exception is PostgresException postgres)
                race.ClaimInsertFailureStates.Enqueue(postgres.SqlState); // Never capture provider messages or parameter values.
            return Task.CompletedTask;
        }
    }

    private sealed class RuntimeContext : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly AsyncServiceScope scope;
        public AuthDbContext Database { get; }
        public IOutboxWriter Outbox => scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        public RuntimeContext(string runtime, DbCommandInterceptor? interceptor = null)
        {
            var services = new ServiceCollection();
            services.AddDbContext<AuthDbContext>(options =>
            {
                options.UseNpgsql(runtime);
                if (interceptor is not null) options.AddInterceptors(interceptor);
            });
            services.AddSingleton<TimeProvider>(new FixedClock());
            services.AddOnboardingPersistenceFoundations(new ConfigurationBuilder().Build());
            provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            scope = provider.CreateAsyncScope();
            Database = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        }

        public OrganisationProvisioningService Service(FixtureIdentity identity,
            ObservedRepository repository, IOutboxWriter? outbox = null) =>
            new(identity, new OrganisationProvisioningTransaction(Database), repository,
                scope.ServiceProvider.GetRequiredService<IIdempotencyStore>(),
                scope.ServiceProvider.GetRequiredService<IAuditWriter>(), outbox ?? Outbox,
                scope.ServiceProvider.GetRequiredService<TimeProvider>(),
                new OrganisationProvisioningOptions { ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromDays(1) });

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private static async Task AssertCountsAsync(AuthDbContext database, int count)
    {
        // These Auth registry tables have accepted explicit RLS exclusions. Their counts
        // prove runtime persistence/atomicity, not tenant-RLS enforcement. Existing grant
        // RLS conformance remains in PostgreSqlMembershipPermissionGrantTests.
        Assert.Equal(count, await database.Users.CountAsync());
        Assert.Equal(count, await database.Organisations.CountAsync());
        Assert.Equal(count, await database.OrganisationMemberships.CountAsync());
        Assert.Equal(count, await database.IdempotencyRecords.CountAsync());
        Assert.Equal(count, await database.AuditEntries.CountAsync());
        Assert.Equal(count, await database.OutboxMessages.CountAsync());
    }

    private static async Task AssertRestrictedRuntimeAsync(AuthDbContext database)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            select session_user,current_user,rolsuper,rolbypassrls,rolcreaterole,rolcreatedb
            from pg_catalog.pg_roles where rolname=current_user
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("zeka_auth_runtime", reader.GetString(0));
        Assert.Equal("zeka_auth_runtime", reader.GetString(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(await reader.ReadAsync());
    }

    private static async Task<string?> TenantAsync(AuthDbContext database)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "select pg_catalog.current_setting('zeka.organisation_id', true)";
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (string)value;
    }

    private sealed class ObservedRepository(AuthDbContext database) : IOrganisationProvisioningRepository
    {
        private readonly OrganisationProvisioningRepository inner = new(database);
        public string? ObservedTenant { get; private set; }
        public Task ValidateOwnerCatalogueAsync(CancellationToken cancellationToken = default) =>
            inner.ValidateOwnerCatalogueAsync(cancellationToken);
        public async Task AddAsync(Organisation organisation, OrganisationMembership ownerMembership,
            CancellationToken cancellationToken = default)
        {
            await inner.AddAsync(organisation, ownerMembership, cancellationToken);
            Assert.NotNull(database.Database.CurrentTransaction);
            ObservedTenant = await TenantAsync(database);
            Assert.Equal(organisation.Id.ToString("D"), ObservedTenant);
        }
    }

    // A controlled admitted identity port persists a real User in the same runtime
    // transaction. This does not exercise Windows owner admission or the CLI identity
    // adapter; its actual UserManager behavior requires separate focused tests.
    private sealed class FixtureIdentity(AuthDbContext database, Guid subject) : IProvisioningIdentitySource
    {
        public int EnsureCalls { get; private set; }
        public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProvisioningIdentity(subject, ProvisioningAuthorityKind.PrivilegedLocalOwner));
        public async Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity, CancellationToken cancellationToken = default)
        {
            Assert.NotNull(database.Database.CurrentTransaction);
            Assert.Equal(subject, identity.SubjectId);
            EnsureCalls++;
            var user = User.Create("provider-owner@example.invalid", "provider-owner", "Provider", "Owner", Now);
            user.Id = subject;
            user.EmailConfirmed = true;
            user.ActivateConfirmedIdentity(Now);
            database.Users.Add(user);
            await database.SaveChangesAsync(cancellationToken);
            return subject;
        }
    }

    private sealed class RefuseAfterOutboxSave(IOutboxWriter inner) : IOutboxWriter
    {
        public int PersistedWrites { get; private set; }
        public async Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default)
        {
            await inner.EnqueueAsync(request, cancellationToken);
            PersistedWrites++;
            throw new InvalidOperationException("Synthetic refusal after outbox persistence.");
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<string> CreateRuntimeAsync()
    {
        var administrator = await fixture.CreateDatabaseAsync();
        await ExecuteAsync(administrator, ReadBootstrapScript());
        // Fresh disposable fixture-only passwords stay in memory; no command logging.
        var migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtimePassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await ExecuteAsync(administrator,
            $"ALTER ROLE zeka_auth_migrator PASSWORD '{migratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{runtimePassword}';");
        var migrator = Connection(administrator, "zeka_auth_migrator", migratorPassword);
        var runtime = Connection(administrator, "zeka_auth_runtime", runtimePassword);
        await using (var migration = new AuthDbContext(Options(migrator)))
        {
            await migration.Database.OpenConnectionAsync();
            await migration.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await migration.GetService<IMigrator>().MigrateAsync();
        }
        await ExecuteAsync(administrator, ReadBootstrapScript());
        return runtime;
    }

    private static DbContextOptions<AuthDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options;

    private static string Connection(string administrator, string username, string password) =>
        new NpgsqlConnectionStringBuilder(administrator)
        {
            Username = username, Password = password, Pooling = false, IncludeErrorDetail = false
        }.ConnectionString;

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ReadBootstrapScript()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The reviewed Auth role bootstrap was not found.");
    }
}
