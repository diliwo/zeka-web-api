using System.Collections.Concurrent;
using System.Data.Common;
using AuthManager.Application.Authentication;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure;
using AuthManager.Infrastructure.Identity.EmailVerification;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Infrastructure.IntegrationTests;

// Reuses the existing CI container fixture. Never execute its unlabelled Testcontainer on the owner workstation.
// Schema comes from the EF model: runtime-role privileges, migration history and manifest conformance are NOT PROVEN here.
[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Evidence","PostgreSqlEmailVerification")]
public sealed class PostgreSqlEmailVerificationExchangeTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Two_Npgsql_contexts_attempt_real_user_locks_and_exactly_one_consumes_the_grant()
    {
        var probe=new LockArrivalProbe();
        await using var host=await PrepareAsync(probe);
        var grant=await VerifyAndCommitAsync(host,await PrepareChallengeAsync(host));
        using var first=host.Services.CreateScope();using var second=host.Services.CreateScope();
        var start=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backends=new ConcurrentBag<int>();
        probe.Arm();
        async Task<bool> Consume(IServiceProvider services)
        {
            await start.Task;
            var database=services.GetRequiredService<AuthDbContext>();
            await using var transaction=await database.Database.BeginTransactionAsync();
            var connection=Assert.IsType<NpgsqlConnection>(database.Database.GetDbConnection());
            backends.Add(connection.ProcessID);
            var consumed=await services.GetRequiredService<IEmailVerificationExchange>().ConsumeRestrictedGrantAsync(
                grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose);
            await transaction.CommitAsync();return consumed;
        }
        var one=Consume(first.ServiceProvider);var two=Consume(second.ServiceProvider);
        start.SetResult();
        var outcomes=await Task.WhenAll(one,two).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(probe.Arrivals==2 && backends.Distinct().Count()==2);
        Assert.True(outcomes.Count(value=>value)==1 && outcomes.Count(value=>!value)==1);
        using var verification=host.Services.CreateScope();
        var db=verification.ServiceProvider.GetRequiredService<AuthDbContext>();
        Assert.True(await db.Set<IdentityUserToken<Guid>>().CountAsync()==0);
        var user=await db.Users.SingleAsync();
        Assert.True(user.EmailConfirmed && user.Status==UserStatus.Active);
        Assert.True(await db.BrowserSessionFamilies.CountAsync()==0);
    }

    [Fact]
    public async Task Npgsql_caller_rollback_restores_pending_verification_and_one_use_consumption()
    {
        await using var host=await PrepareAsync();
        var challenge=await PrepareChallengeAsync(host);
        using(var scope=host.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var transaction=await db.Database.BeginTransactionAsync();
            var result=await scope.ServiceProvider.GetRequiredService<IEmailVerificationExchange>()
                .VerifyAsync(host.Subject,challenge.GetTokenForDelivery());
            Assert.True(result.Outcome==EmailVerificationOutcome.Success && result.Grant is not null);
            await transaction.RollbackAsync();
        }
        using(var scope=host.Services.CreateScope())
        {
            var db=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var user=await db.Users.SingleAsync();
            Assert.True(!user.EmailConfirmed && user.Status==UserStatus.PendingVerification);
            Assert.True(await db.Set<IdentityUserToken<Guid>>().CountAsync()==1);
        }
        var grant=await VerifyAndCommitAsync(host,challenge);
        async Task<bool> Consume(bool commit)
        {
            using var scope=host.Services.CreateScope();
            var db=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await using var transaction=await db.Database.BeginTransactionAsync();
            var consumed=await scope.ServiceProvider.GetRequiredService<IEmailVerificationExchange>().ConsumeRestrictedGrantAsync(
                grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose);
            if(commit)await transaction.CommitAsync();else await transaction.RollbackAsync();
            return consumed;
        }
        Assert.True(await Consume(false));
        Assert.True(await Consume(true));
        Assert.False(await Consume(true));
    }

    private async Task<Host> PrepareAsync(LockArrivalProbe? probe=null)
    {
        var connection=await fixture.CreateDatabaseAsync();
        await using(var db=new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options))
            await db.Database.EnsureCreatedAsync();
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();
        builder.Services.AddDbContext<AuthDbContext>(options=>
        {
            // No retry strategy: each test owns one explicit transaction/attempt.
            options.UseNpgsql(connection,provider=>provider.CommandTimeout(10));
            if(probe is not null)options.AddInterceptors(probe);
        });
        builder.ConfigureMicrosoftIdentity();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.Configure<EmailVerificationExchangeOptions>(options=>options.RestrictedGrantLifetime=TimeSpan.FromMinutes(5));
        builder.Services.AddScoped<IEmailVerificationExchange,EmailVerificationExchange>();
        var services=builder.Services.BuildServiceProvider();
        try
        {
            using var scope=services.CreateScope();
            var user=User.Create(Guid.NewGuid().ToString("N")+"@example.test",Guid.NewGuid().ToString("N"),"Provider","Test",DateTimeOffset.UtcNow);
            var result=await scope.ServiceProvider.GetRequiredService<UserManager<User>>()
                .CreateAsync(user,Guid.NewGuid().ToString("N")+"aA1!");
            Assert.True(result.Succeeded);
            return new(services,user.Id);
        }
        catch{await services.DisposeAsync();throw;}
    }
    private static async Task<EmailVerificationChallenge> PrepareChallengeAsync(Host host)
    {
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var result=await scope.ServiceProvider.GetRequiredService<IEmailVerificationExchange>().PrepareAsync(host.Subject);
        await transaction.CommitAsync();return result;
    }
    private static async Task<RestrictedOnboardingGrant> VerifyAndCommitAsync(Host host,EmailVerificationChallenge challenge)
    {
        using var scope=host.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction=await db.Database.BeginTransactionAsync();
        var result=await scope.ServiceProvider.GetRequiredService<IEmailVerificationExchange>()
            .VerifyAsync(host.Subject,challenge.GetTokenForDelivery());
        Assert.True(result.Outcome==EmailVerificationOutcome.Success && result.Grant is not null);
        await transaction.CommitAsync();return result.Grant!;
    }
    private sealed class LockArrivalProbe : DbCommandInterceptor
    {
        private readonly TaskCompletionSource both=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool armed;private int arrivals;
        public int Arrivals=>Volatile.Read(ref arrivals);
        public void Arm()=>armed=true;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
        {
            if(!armed || !command.CommandText.Contains("FOR UPDATE",StringComparison.OrdinalIgnoreCase))return result;
            if(Interlocked.Increment(ref arrivals)==2)both.TrySetResult();
            // This observes two commands before submission; it does not claim pg_stat_activity lock-wait observation.
            await both.Task.WaitAsync(TimeSpan.FromSeconds(10),cancellationToken);
            return result;
        }
    }
    private sealed class Host(ServiceProvider services,Guid subject) : IAsyncDisposable
    {
        public ServiceProvider Services=>services;public Guid Subject=>subject;
        public ValueTask DisposeAsync()=>services.DisposeAsync();
    }
}
