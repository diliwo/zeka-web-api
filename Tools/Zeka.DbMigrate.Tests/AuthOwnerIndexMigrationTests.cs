using System.Text.Json;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Zeka.DbMigrate.Tests;

[Trait("Issue", "ZEK-50")]
[Trait("Evidence", "PostgreSQL17")]
public sealed class AuthOwnerIndexMigrationTests(AuthOwnerIndexPostgreSqlFixture fixture, ITestOutputHelper output)
    : IClassFixture<AuthOwnerIndexPostgreSqlFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Historical_onboarding_then_index_commits_both_exact_steps_with_unchanged_security()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await BaselineAsync(ct.Token);
        var onboarding = await database.ExecuteAsync(ct.Token);
        Assert.Equal(0, onboarding.ExitCode);
        Assert.Equal(AuthOnboardingSqlArtifacts.MigrationSha256, onboarding.Evidence.AtomicAuth!.MigrationSqlSha256);
        Assert.Equal(AuthOnboardingSqlArtifacts.ManifestSha256, onboarding.Evidence.AtomicAuth.ManifestSha256);
        await database.VerifyV20Async(ct.Token); // Active assembly embeds v21; the original v20 is still actually verified.
        var index = await database.ExecuteOwnerIndexAsync(ct.Token);
        Assert.Equal(0, index.ExitCode);
        Assert.Equal("ATOMIC_AUTH_APPLIED", index.Evidence.Classification);
        Assert.Equal(AuthExactMigrationStep.OwnerMigrationSha256, index.Evidence.AtomicAuth!.MigrationSqlSha256);
        Assert.Equal(AuthExactMigrationStep.NoGrantSha256, index.Evidence.AtomicAuth.GrantSqlSha256);
        Assert.True(index.Evidence.AtomicAuth.CommitAcknowledged);
        await database.VerifyV21Async(ct.Token);
        await database.AssertRuntimeDenialsAsync(ct.Token);
        var committed = await database.DigestAsync(ct.Token);
        var repeated = await database.ExecuteOwnerIndexAsync(ct.Token);
        Assert.Equal(0, repeated.ExitCode);
        Assert.Equal("ATOMIC_AUTH_ALREADY_CURRENT", repeated.Evidence.Classification);
        Assert.Equal(committed, await database.DigestAsync(ct.Token));
        await using var owner = await fixture.OpenOwnerAsync(database.Name, ct.Token);
        Assert.Equal(1L, await ScalarAsync(owner, """
            SELECT count(*) FROM pg_catalog.pg_index i JOIN pg_catalog.pg_class x ON x.oid=i.indexrelid
            WHERE i.indrelid='public."OrganisationMemberships"'::regclass
              AND x.relname='IX_OrganisationMemberships_OrganisationId'
              AND NOT i.indisunique AND i.indpred IS NULL
            """, ct.Token));
        output.WriteLine("Fresh PostgreSQL17: historical v20 and additive v21 both verified; default bridge, protected file and scoped cleanup qualification.");
    }

    [Fact]
    public async Task Fresh_atomic_chain_and_established_upgrade_have_identical_schema_security_and_history()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var fresh = await BaselineAsync(ct.Token);
        Assert.Equal(0, (await fresh.ExecuteAsync(ct.Token)).ExitCode);
        Assert.Equal(0, (await fresh.ExecuteOwnerIndexAsync(ct.Token)).ExitCode);
        var upgraded = await fixture.NewDatabaseAsync(ct.Token);
        await upgraded.PrepareEfBootstrapCompatibilityAsync(ct.Token);
        await upgraded.VerifyV20Async(ct.Token);
        Assert.Equal(0, (await upgraded.ExecuteOwnerIndexAsync(ct.Token)).ExitCode);
        await fresh.VerifyV21Async(ct.Token);
        await upgraded.VerifyV21Async(ct.Token);
        var first = await fresh.CaptureCatalogAsync(ct.Token);
        var second = await upgraded.CaptureCatalogAsync(ct.Token);
        Assert.Equal(first.QuerySha256, second.QuerySha256);
        Assert.Equal(NormalizeHistoryVersion(first.Rows), NormalizeHistoryVersion(second.Rows));
    }

    [Fact]
    public async Task Existing_duplicate_active_owners_refuse_without_modifying_data_or_history()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        var subjects = await SeedAsync(database, twoActiveOwners: true, ct.Token);
        var before = await database.DigestAsync(ct.Token);
        var result = await database.ExecuteOwnerIndexAsync(ct.Token);
        Assert.Equal(27, result.ExitCode);
        Assert.Equal("ATOMIC_AUTH_ROLLED_BACK", result.Evidence.Classification);
        Assert.True(result.Evidence.AtomicAuth!.RollbackConfirmed);
        Assert.Equal(before, await database.DigestAsync(ct.Token));
        await database.VerifyV20Async(ct.Token);
        await using var owner = await fixture.OpenOwnerAsync(database.Name, ct.Token);
        Assert.Equal(2L, await ScalarAsync(owner, """
            SELECT count(*) FROM public."OrganisationMemberships"
            WHERE "OrganisationId"=@organisation AND "PermissionSetId"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid AND "Status"=1
            """, ct.Token, subjects.Organisation));
    }

    [Fact]
    public async Task Restricted_runtime_cannot_insert_or_reactivate_a_second_active_owner()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        Assert.Equal(0, (await database.ExecuteOwnerIndexAsync(ct.Token)).ExitCode);
        var subjects = await SeedAsync(database, twoActiveOwners: false, ct.Token);
        await using var runtime = new NpgsqlConnection(fixture.RuntimeConnection(database.Name));
        await runtime.OpenAsync(ct.Token);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => InsertOwnerAsync(runtime, subjects, 1, ct.Token));
        Assert.Equal("23505", denied.SqlState);
        Assert.Equal(OrganisationOwnerIndexContract.Name, denied.ConstraintName);
        await InsertOwnerAsync(runtime, subjects, 2, ct.Token); // The approved index is partial, not a ban on other membership states.
        await using var activate = new NpgsqlCommand("""
            UPDATE public."OrganisationMemberships" SET "Status"=1
            WHERE "OrganisationId"=@organisation AND "UserId"=@user
            """, runtime);
        activate.Parameters.AddWithValue("organisation", subjects.Organisation);
        activate.Parameters.AddWithValue("user", subjects.Second);
        var activationDenied = await Assert.ThrowsAsync<PostgresException>(() => activate.ExecuteNonQueryAsync(ct.Token));
        Assert.Equal("23505", activationDenied.SqlState);
        Assert.Equal(OrganisationOwnerIndexContract.Name, activationDenied.ConstraintName);
        Assert.Equal(1L, await ScalarAsync(runtime, """
            SELECT count(*) FROM public."OrganisationMemberships" m
            JOIN public."Organisations" o ON o."Id"=m."OrganisationId"
            WHERE o."Id"=@organisation AND m."PermissionSetId"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid
              AND m."Status"=1 AND m."UserId"=o."OwnerUserId"
            """, ct.Token, subjects.Organisation));
        await database.VerifyV21Async(ct.Token);
    }

    [Fact]
    public async Task Verification_failure_rolls_back_the_index_and_history_together()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        var before = await database.DigestAsync(ct.Token);
        var result = await database.ExecuteOwnerIndexAsync(ct.Token, new AuthOnboardingHooks(
            AfterVerification: (_, _, _) => throw new InvalidOperationException("CONTROLLED_INDEX_VERIFICATION_FAILURE")));
        Assert.Equal(27, result.ExitCode);
        Assert.True(result.Evidence.AtomicAuth!.RollbackConfirmed);
        Assert.False(result.Evidence.AtomicAuth.CommitAttempted);
        Assert.Equal(before, await database.DigestAsync(ct.Token));
        await database.VerifyV20Async(ct.Token);
    }

    [Fact]
    public async Task Unexpected_default_grant_is_rejected_before_any_index_write()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        await database.AddUnexpectedDefaultGrantAsync(ct.Token);
        var before = await database.DigestAsync(ct.Token);
        var result = await database.ExecuteOwnerIndexAsync(ct.Token);
        Assert.Equal(27, result.ExitCode);
        Assert.Equal("ATOMIC_AUTH_DEFAULT_PRIVILEGES_REJECTED", result.Evidence.ResultCode);
        Assert.False(result.Evidence.AtomicAuth!.CommitAttempted);
        Assert.Equal(before, await database.DigestAsync(ct.Token));
    }

    [Fact]
    public async Task Concurrent_index_executors_wait_then_recheck_the_committed_history()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = database.ExecuteOwnerIndexAsync(ct.Token, new AuthOnboardingHooks(AfterLock: async (connection, _, token) =>
        { entered.TrySetResult(connection.ProcessID); await release.Task.WaitAsync(token); }));
        Task<EngineResult>? second = null;
        try
        {
            var pid = await entered.Task.WaitAsync(ct.Token);
            second = database.ExecuteOwnerIndexAsync(ct.Token);
            await database.AssertSameAdvisoryLockWaiterAsync(pid, ct.Token);
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            var results = await Task.WhenAll(first, second);
            Assert.All(results, result => Assert.Equal(0, result.ExitCode));
            Assert.Equal("ATOMIC_AUTH_APPLIED", results[0].Evidence.Classification);
            Assert.Equal("ATOMIC_AUTH_ALREADY_CURRENT", results[1].Evidence.Classification);
            await database.VerifyV21Async(ct.Token);
        }
        finally
        {
            release.TrySetResult();
            try { await Task.WhenAll(second is null ? [first] : [first, second]).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { /* Preserve the original provider result. */ }
        }
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_is_inspected_without_replaying_the_index()
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        var commits = 0;
        var result = await database.ExecuteOwnerIndexAsync(ct.Token, new AuthOnboardingHooks(CommitAsync: async (transaction, token) =>
        {
            await transaction.CommitAsync(token);
            Interlocked.Increment(ref commits);
            throw new NpgsqlException("CONTROLLED_INDEX_COMMIT_ACK_LOSS");
        }));
        Assert.Equal(28, result.ExitCode);
        Assert.Equal(1, commits);
        Assert.True(result.Evidence.AtomicAuth!.CommitAttempted);
        Assert.False(result.Evidence.AtomicAuth.CommitAcknowledged);
        Assert.False(result.Evidence.AtomicAuth.ReplayAttempted);
        Assert.Equal("COMMITTED_VERIFIED", result.Evidence.AtomicAuth.DurableState);
        await database.VerifyV21Async(ct.Token);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Runtime_catalog_guard_rejects_a_weaker_same_named_index(bool unique, int status)
    {
        using var ct = Deadline();
        output.WriteLine(JsonSerializer.Serialize(new { container = fixture.ContainerId, volume = fixture.VolumeName,
            image = fixture.ImageIdentity, defaultBridge = true, dedicatedBridge = false, protectedPasswordFile = true }));
        var database = await OnboardingAsync(ct.Token);
        await using var owner = await fixture.OpenOwnerAsync(database.Name, ct.Token);
        await using var create = new NpgsqlCommand($"""
            CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX "UX_OrganisationMemberships_ActiveOwner"
            ON public."OrganisationMemberships" ("OrganisationId")
            WHERE "PermissionSetId"='d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid AND "Status"={status}
            """, owner);
        await create.ExecuteNonQueryAsync(ct.Token);
        await using var context = Context(owner);
        Assert.False(await OrganisationOwnerIndexContract.IsInstalledAsync(context, ct.Token));
        var result = await database.ExecuteOwnerIndexAsync(ct.Token);
        Assert.Equal(25, result.ExitCode);
        Assert.Equal("ATOMIC_AUTH_BASELINE_SCHEMA_REJECTED", result.Evidence.ResultCode);
    }

    private async Task<AuthOnboardingAtomicDatabase> BaselineAsync(CancellationToken ct)
    {
        var database = await fixture.NewDatabaseAsync(ct);
        await database.PrepareBaselineAsync(ct);
        return database;
    }
    private async Task<AuthOnboardingAtomicDatabase> OnboardingAsync(CancellationToken ct)
    {
        var database = await BaselineAsync(ct);
        Assert.Equal(0, (await database.ExecuteAsync(ct)).ExitCode);
        await database.VerifyV20Async(ct);
        return database;
    }
    private async Task<(Guid Organisation, Guid First, Guid Second)> SeedAsync(AuthOnboardingAtomicDatabase database,
        bool twoActiveOwners, CancellationToken ct)
    {
        await using var owner = await fixture.OpenOwnerAsync(database.Name, ct);
        await using var context = Context(owner);
        var first = NewUser();
        var second = NewUser();
        var organisation = Organisation.Create(Guid.NewGuid(), "Synthetic owner index fixture", first.Id, Now);
        Assert.True(organisation.Activate(Now));
        context.Users.AddRange(first, second);
        context.Organisations.Add(organisation);
        context.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, first.Id, Now));
        if (twoActiveOwners)
            context.OrganisationMemberships.Add(OrganisationMembership.CreateOwner(Guid.NewGuid(), organisation.Id, second.Id, Now));
        await context.SaveChangesAsync(ct);
        return (organisation.Id, first.Id, second.Id);
    }
    private static User NewUser()
    {
        var email = "owner-index-" + Guid.NewGuid().ToString("N") + "@example.invalid";
        var user = User.Create(email, email, "Synthetic", "Fixture", Now);
        user.EmailConfirmed = true;
        user.ActivateConfirmedIdentity(Now);
        return user;
    }
    private static async Task InsertOwnerAsync(NpgsqlConnection connection, (Guid Organisation, Guid First, Guid Second) values,
        int status, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."OrganisationMemberships"
              ("Id","OrganisationId","UserId","PermissionSetId","Status","JoinedAtUtc","ConcurrencyVersion")
            VALUES (@id,@organisation,@user,'d8eceeeb-b796-4d77-92a5-6a11fab10a01'::uuid,@status,@joined,1)
            """, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organisation", values.Organisation);
        command.Parameters.AddWithValue("user", values.Second);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("joined", Now);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct, Guid? organisation = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (organisation is { } id) command.Parameters.AddWithValue("organisation", id);
        return await command.ExecuteScalarAsync(ct);
    }
    private static AuthDbContext Context(NpgsqlConnection connection) => new(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options);
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromMinutes(3));
    private static string[] NormalizeHistoryVersion(IEnumerable<string> rows) => rows.Select(row =>
    {
        using var json = JsonDocument.Parse(row);
        if (json.RootElement[0].GetString() != "history") return row;
        var version = json.RootElement[2].GetString();
        Assert.Contains(version, new[] { "8.0.8", "8.0.30" });
        return JsonSerializer.Serialize(new[] { "history", json.RootElement[1].GetString(), "<approved-EF-version>" });
    }).Order(StringComparer.Ordinal).ToArray();
}

[Trait("Issue", "ZEK-50")]
[Trait("Evidence", "SourceOnly")]
public sealed class AuthOwnerIndexSourceTests
{
    [Fact]
    public void Successor_is_one_additive_partial_index_and_existing_lookup_index_is_preserved()
    {
        using var database = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(new NpgsqlConnection()).Options);
        var assembly = database.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[OrganisationOwnerIndexContract.MigrationId],
            database.Database.ProviderName!);
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal(4, migration.UpOperations.Count);
        Assert.True(index.IsUnique);
        Assert.Equal(OrganisationOwnerIndexContract.Name, index.Name);
        Assert.Equal(new[] { "OrganisationId" }, index.Columns);
        Assert.Equal(OrganisationOwnerIndexContract.Filter, index.Filter);
        Assert.DoesNotContain(migration.UpOperations, operation => operation is DropIndexOperation or AlterColumnOperation or CreateTableOperation);
        var model = database.Model.FindEntityType(typeof(OrganisationMembership))!;
        Assert.Contains(model.GetIndexes(), current => !current.IsUnique && current.GetDatabaseName() == "IX_OrganisationMemberships_OrganisationId");
        Assert.Contains(model.GetIndexes(), current => current.IsUnique && current.GetDatabaseName() == OrganisationOwnerIndexContract.Name);
        Assert.Equal(new[] { AuthOnboardingSqlArtifacts.FromMigration, AuthOnboardingSqlArtifacts.TargetMigration, AuthExactMigrationStep.OwnerTarget },
            database.Database.GetMigrations().TakeLast(3));
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("20261124170001_Unapproved")]
    public void Only_the_two_reviewed_exact_targets_can_load_executable_artifacts(string target)
        => Assert.Throws<InvalidOperationException>(() => AuthExactMigrationStep.Load(target));
}
