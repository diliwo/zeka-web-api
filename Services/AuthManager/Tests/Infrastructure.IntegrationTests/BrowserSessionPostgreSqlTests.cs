using System.Security.Cryptography;
using AuthManager.Application.Authentication;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "89")]
public sealed class BrowserSessionPostgreSqlTests(PostgreSqlFixture fixture)
{
    private const string Email = "ada.session@example.invalid";
    private const string Password = "Synthetic-Only!89";

    [Fact]
    public async Task Two_connections_rotate_once_and_predecessor_reuse_revokes_the_successor_and_sid()
    {
        await using var test = await PrepareAsync();
        var initial = await test.SignInAsync();
        Assert.Equal(BrowserSessionOutcome.Success, initial.Outcome);
        using var first = test.Services.CreateScope();
        using var second = test.Services.CreateScope();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<BrowserSessionResult> Restore(IServiceProvider services)
        {
            await start.Task;
            return await services.GetRequiredService<IBrowserSessions>().RestoreAsync(initial.RefreshSecret, default);
        }
        var one = Restore(first.ServiceProvider); var two = Restore(second.ServiceProvider);
        start.SetResult();
        var results = await Task.WhenAll(one, two);
        Assert.Single(results.Where(x => x.Outcome == BrowserSessionOutcome.Success));
        Assert.Single(results.Where(x => x.Outcome == BrowserSessionOutcome.Unauthenticated));
        using var verify = test.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AuthDbContext>();
        var family = await db.BrowserSessionFamilies.SingleAsync();
        Assert.NotNull(family.RevokedAtUtc);
        Assert.Equal(2, await db.BrowserRefreshCredentials.CountAsync());
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, await verify.ServiceProvider.GetRequiredService<IBrowserSessions>()
            .CheckActiveAsync(initial.SubjectId, family.Id, default));
        var winner = results.Single(x => x.Outcome == BrowserSessionOutcome.Success);
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await verify.ServiceProvider.GetRequiredService<IBrowserSessions>()
            .RestoreAsync(winner.RefreshSecret, default)).Outcome);
        var hashes = await db.BrowserRefreshCredentials.Select(x => x.Hash).ToListAsync();
        Assert.All(hashes, value => { Assert.Equal(64, value.Length); Assert.DoesNotContain(initial.RefreshSecret!, value); });
        var audit = await db.AuditEntries.ToListAsync();
        Assert.Contains(audit, x => x.Outcome == "refresh_reuse");
        Assert.All(audit, entry => Assert.DoesNotContain(initial.RefreshSecret!, entry.Metadata));
    }

    [Fact]
    public async Task Idle_resets_only_on_rotation_absolute_deadline_is_fixed_and_exact_boundary_denies()
    {
        await using var test = await PrepareAsync();
        var first = await test.SignInAsync();
        Assert.Equal(test.Clock.Now.AddMinutes(5), first.ExpiresAtUtc);
        using var scope = test.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrowserSessions>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var family = await db.BrowserSessionFamilies.AsNoTracking().SingleAsync();
        var absolute = family.AbsoluteExpiresAtUtc;
        test.Clock.Now = test.Clock.Now.AddHours(7);
        Assert.Equal(BrowserSessionOutcome.Success, await sessions.CheckActiveAsync(first.SubjectId, family.Id, default));
        Assert.Equal(family.IdleExpiresAtUtc, (await db.BrowserSessionFamilies.AsNoTracking().SingleAsync()).IdleExpiresAtUtc);
        var rotated = await sessions.RestoreAsync(first.RefreshSecret, default);
        Assert.Equal(BrowserSessionOutcome.Success, rotated.Outcome);
        Assert.Equal(test.Clock.Now.AddHours(8), rotated.CookieExpiresAtUtc);
        for (var i = 0; i < 22; i++)
        {
            test.Clock.Now = test.Clock.Now.AddHours(7);
            rotated = await sessions.RestoreAsync(rotated.RefreshSecret, default);
            Assert.Equal(BrowserSessionOutcome.Success, rotated.Outcome);
        }
        Assert.Equal(absolute, rotated.CookieExpiresAtUtc);
        test.Clock.Now = absolute;
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await sessions.RestoreAsync(rotated.RefreshSecret, default)).Outcome);
        Assert.Equal(absolute, (await db.BrowserSessionFamilies.AsNoTracking().SingleAsync()).AbsoluteExpiresAtUtc);
    }

    [Fact]
    public async Task Lost_logout_response_can_be_confirmed_only_with_exact_unexpired_cookie()
    {
        await using var test = await PrepareAsync();
        var first = await test.SignInAsync();
        using var scope = test.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IBrowserSessions>();
        Assert.Equal(BrowserSessionOutcome.Success, await sessions.LogoutAsync(first.RefreshSecret, default));
        Assert.Equal(BrowserSessionOutcome.Success, await sessions.LogoutAsync(first.RefreshSecret, default));
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, await sessions.LogoutAsync(null, default));
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, await sessions.LogoutAsync(new string('a', 43), default));
        test.Clock.Now = first.CookieExpiresAtUtc;
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, await sessions.LogoutAsync(first.RefreshSecret, default));
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("password_reset")]
    [InlineData("suspension")]
    public async Task Identity_invalidation_denies_old_signed_bearer_session(string change)
    {
        await using var test = await PrepareAsync();
        var first = await test.SignInAsync();
        using (var update = test.Services.CreateScope())
        {
            var users = update.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await users.FindByEmailAsync(Email))!;
            if (change == "stamp") Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
            else if (change == "password_reset")
            {
                var reset = await users.GeneratePasswordResetTokenAsync(user);
                Assert.True((await users.ResetPasswordAsync(user, reset, "Synthetic-Replacement!90")).Succeeded);
            }
            else
            {
                var db = update.ServiceProvider.GetRequiredService<AuthDbContext>();
                db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Suspended;
                await db.SaveChangesAsync();
            }
        }
        using var scope = test.Services.CreateScope();
        var dbVerify = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var family = await dbVerify.BrowserSessionFamilies.AsNoTracking().SingleAsync();
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
            .CheckActiveAsync(first.SubjectId, family.Id, default));
        Assert.NotNull((await dbVerify.BrowserSessionFamilies.AsNoTracking().SingleAsync()).RevokedAtUtc);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unconfirmed")]
    [InlineData("pending")]
    [InlineData("suspended")]
    [InlineData("locked")]
    [InlineData("password")]
    public async Task Sign_in_is_generic_and_never_creates_a_family_for_rejected_users(string reason)
    {
        await using var test = await PrepareAsync();
        using var scope = test.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync();
        if (reason == "unconfirmed") user.EmailConfirmed = false;
        if (reason == "pending") db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.PendingVerification;
        if (reason == "suspended") db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Suspended;
        if (reason == "locked") user.LockoutEnd = test.Clock.Now.AddHours(1);
        await db.SaveChangesAsync();
        var result = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().SignInAsync(
            reason == "unknown" ? "unknown@example.invalid" : Email, reason == "password" ? "invalid" : Password, null, default);
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, result.Outcome);
        Assert.Null(result.AccessToken); Assert.Null(result.RefreshSecret);
        Assert.Empty(await db.BrowserSessionFamilies.ToListAsync());
    }

    [Fact]
    public async Task Missing_hash_authority_returns_unavailable_without_session_or_token()
    {
        await using var test = await PrepareAsync();
        File.Delete(test.KeyPath);
        var result = await test.SignInAsync();
        Assert.Equal(BrowserSessionOutcome.Unavailable, result.Outcome);
        Assert.Null(result.AccessToken); Assert.Null(result.RefreshSecret);
    }

    [Fact]
    public async Task Lost_commit_acknowledgement_never_replays_rotation_and_old_cookie_reuse_revokes_family()
    {
        var failure = new LostCommit();
        await using var test = await PrepareAsync(failure);
        var first = await test.SignInAsync();
        Assert.Equal(BrowserSessionOutcome.Success, first.Outcome);
        failure.Armed = true;
        using (var scope = test.Services.CreateScope())
        {
            var ambiguous = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().RestoreAsync(first.RefreshSecret, default);
            Assert.Equal(BrowserSessionOutcome.Unavailable, ambiguous.Outcome);
            Assert.Null(ambiguous.AccessToken); Assert.Null(ambiguous.RefreshSecret);
            Assert.Equal(1, failure.Failures);
        }
        using var verify = test.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.Equal(2, await db.BrowserRefreshCredentials.CountAsync());
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await verify.ServiceProvider.GetRequiredService<IBrowserSessions>()
            .RestoreAsync(first.RefreshSecret, default)).Outcome);
        Assert.NotNull((await db.BrowserSessionFamilies.SingleAsync()).RevokedAtUtc);
    }

    [Fact]
    public async Task Migration_and_repeated_bootstrap_preserve_manifest_and_restricted_runtime_session_writes()
    {
        await using var test = await PrepareAsync();
        for (var i = 0; i < 2; i++) await BootstrapAsync(test.Connection);
        await using (var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(test.Connection).Options))
            await Zeka.PersistenceSecurity.RlsSecurityManifestVerifier.VerifyAsync(db, typeof(AuthDbContext).Assembly);
        // This is the synthetic container's superuser connection, with runtime role selected before any application SQL.
        var runtime = new NpgsqlConnectionStringBuilder(test.Connection) { Options = "-c role=zeka_auth_runtime", Pooling = false }.ConnectionString;
        using var scope = test.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        dbContext.Database.SetConnectionString(runtime);
        var signedIn = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().SignInAsync(Email, Password, null, default);
        Assert.Equal(BrowserSessionOutcome.Success, signedIn.Outcome);
        Assert.Equal(BrowserSessionOutcome.Success, (await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
            .RestoreAsync(signedIn.RefreshSecret, default)).Outcome);
        await Assert.ThrowsAsync<PostgresException>(() => dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE \"BrowserSessionFamilies\" SET \"SecurityStamp\" = 'forged'"));
    }

    [Fact]
    public async Task Wrong_password_locks_identity()
    {
        await using var test = await PrepareAsync();
        for (var i = 0; i < 3; i++)
        {
            using var scope = test.Services.CreateScope();
            Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
                .SignInAsync(Email, "wrong", null, default)).Outcome);
        }
        using (var scope = test.Services.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().FindByEmailAsync(Email);
            Assert.NotNull(user!.LockoutEnd);
        }
        Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await test.SignInAsync()).Outcome);
    }

    [Fact]
    public async Task Idle_expiry_denies_at_exact_boundary_and_sign_in_revokes_identifiable_previous_family()
    {
        await using var test = await PrepareAsync();
        var first = await test.SignInAsync();
        using (var scope = test.Services.CreateScope())
        {
            var replacement = await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
                .SignInAsync(Email, Password, first.RefreshSecret, default);
            Assert.Equal(BrowserSessionOutcome.Success, replacement.Outcome);
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Assert.Single(await db.BrowserSessionFamilies.Where(x => x.RevokedAtUtc != null).ToListAsync());
            test.Clock.Now = replacement.CookieExpiresAtUtc;
            Assert.Equal(BrowserSessionOutcome.Unauthenticated, (await scope.ServiceProvider.GetRequiredService<IBrowserSessions>()
                .RestoreAsync(replacement.RefreshSecret, default)).Outcome);
        }
    }

    private async Task<TestHost> PrepareAsync(LostCommit? lostCommit = null)
    {
        // This connection is exclusively a newly named database in the test-owned PostgreSQL container.
        var connection = await fixture.CreateDatabaseAsync();
        await BootstrapAsync(connection);
        await using (var db = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options))
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await db.Database.MigrateAsync();
        }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AuthDbContext>(options =>
        {
            options.UseNpgsql(connection, provider => provider.EnableRetryOnFailure());
            if (lostCommit is not null) options.AddInterceptors(lostCommit);
        });
        builder.ConfigureMicrosoftIdentity();
        builder.Services.AddOnboardingPersistenceFoundations(builder.Configuration);
        var clock = new Clock();
        var keyPath = Path.Combine(Path.GetTempPath(), $"zeka-session-test-{Guid.NewGuid():N}.key");
        await File.WriteAllBytesAsync(keyPath, RandomNumberGenerator.GetBytes(32));
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(new BrowserSessionPolicy { HashKeyReference = keyPath, AccessLifetime = TimeSpan.FromMinutes(5),
            IdleLifetime = TimeSpan.FromHours(8), AbsoluteLifetime = TimeSpan.FromDays(7) });
        builder.Services.AddSingleton<ISessionAccessTokenIssuer, TokenIssuer>();
        builder.Services.AddScoped<IBrowserSessions, BrowserSessions>();
        var services = builder.Services.BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var user = User.Create(Email, Email, "Ada", "Example", clock.Now);
            user.EmailConfirmed = true;
            var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await manager.CreateAsync(user, Password)).Succeeded);
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.Entry(user).Property(x => x.Status).CurrentValue = UserStatus.Active;
            await db.SaveChangesAsync();
        }
        return new(services, clock, keyPath, connection);
    }
    private static async Task BootstrapAsync(string connection)
    {
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        await using var command = sql.CreateCommand();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Deployments/database/bootstrap-auth-roles.sql")))
            directory = directory.Parent;
        command.CommandText = await File.ReadAllTextAsync(Path.Combine(directory!.FullName, "Deployments/database/bootstrap-auth-roles.sql"));
        await command.ExecuteNonQueryAsync();
    }
    private sealed class LostCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Failures { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Armed) { Armed = false; Failures++; throw new NpgsqlException("Synthetic lost commit acknowledgement", new TimeoutException()); }
            return Task.CompletedTask;
        }
    }
    private sealed class TokenIssuer : ISessionAccessTokenIssuer
    {
        public Task<string> IssueSessionAsync(Guid subjectId, Guid sessionId, TimeSpan lifetime, CancellationToken cancellationToken)
            => Task.FromResult("synthetic-test-bearer");
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class TestHost(ServiceProvider services, Clock clock, string keyPath, string connection) : IAsyncDisposable
    {
        public ServiceProvider Services => services;
        public Clock Clock => clock;
        public string KeyPath => keyPath;
        public string Connection => connection;
        public async Task<BrowserSessionResult> SignInAsync()
        {
            using var scope = services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IBrowserSessions>().SignInAsync(Email, Password, null, default);
        }
        public async ValueTask DisposeAsync() { await services.DisposeAsync(); File.Delete(keyPath); }
    }
}
