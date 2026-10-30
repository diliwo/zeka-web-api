using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AuthManager.Application.Authentication;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.CustomTokenProviders;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Identity.EmailVerification;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.IntegrationTests;

// SQLite proves functional persistence/rollback only, not PostgreSQL locking or runtime privileges.
public sealed class EmailVerificationExchangeTests
{
    [Fact]
    public async Task Genuine_identity_roundtrip_activates_and_persists_only_hashes_with_restricted_five_minute_grant()
    {
        await using var fixture=await Fixture.CreateAsync();
        var challenge=await fixture.PrepareAsync();
        var grant=await fixture.VerifyAsync(challenge);
        fixture.Database.ChangeTracker.Clear();
        var user=await fixture.Database.Users.SingleAsync();
        Assert.True(user.EmailConfirmed && user.Status==UserStatus.Active);
        Assert.True(grant.SubjectId==user.Id && grant.GrantId!=Guid.Empty);
        Assert.True(grant.Purpose=="organisation_onboarding" && grant.ExpiresAtUtc-grant.IssuedAtUtc==TimeSpan.FromMinutes(5));
        Assert.True(Regex.IsMatch(grant.GetCredentialForExchange(),"^[A-Za-z0-9_-]{43}$"));
        var values=await fixture.Database.Set<IdentityUserToken<Guid>>().Select(x=>x.Value!).ToListAsync();
        Assert.True(values.Count==1 && values.All(x=>!x.Contains(challenge.GetTokenForDelivery(),StringComparison.Ordinal) &&
            !x.Contains(grant.GetCredentialForExchange(),StringComparison.Ordinal)));
        Assert.False(JsonSerializer.Serialize(challenge).Contains(challenge.GetTokenForDelivery(),StringComparison.Ordinal));
        Assert.False(JsonSerializer.Serialize(grant).Contains(grant.GetCredentialForExchange(),StringComparison.Ordinal));
        Assert.False(challenge.ToString().Contains(challenge.GetTokenForDelivery(),StringComparison.Ordinal));
        Assert.False(grant.ToString().Contains(grant.GetCredentialForExchange(),StringComparison.Ordinal));
        Assert.True(await fixture.Database.BrowserSessionFamilies.CountAsync()==0);
    }

    [Theory]
    [InlineData("wrong-token")]
    [InlineData("wrong-purpose")]
    [InlineData("expired")]
    [InlineData("invalidated-identity-proof")]
    public async Task Invalid_challenges_do_not_activate_or_issue_a_grant(string reason)
    {
        await using var fixture=await Fixture.CreateAsync();
        var challenge=await fixture.PrepareAsync();
        var token=challenge.GetTokenForDelivery();
        if(reason=="wrong-token")token="not-a-valid-identity-proof";
        if(reason=="expired")fixture.Clock.Now=challenge.ExpiresAtUtc;
        if(reason=="wrong-purpose")
        {
            var row=await fixture.Database.Set<IdentityUserToken<Guid>>().SingleAsync();
            var json=JsonNode.Parse(row.Value!)!;json["Purpose"]="not-email-verification";
            row.Value=json.ToJsonString();await fixture.Database.SaveChangesAsync();
        }
        if(reason=="invalidated-identity-proof")
            Assert.True((await fixture.Users.UpdateSecurityStampAsync((await fixture.Users.FindByIdAsync(fixture.Subject.ToString("D")))!)).Succeeded);
        await using(var transaction=await fixture.Database.Database.BeginTransactionAsync())
        {
            var result=await fixture.Exchange.VerifyAsync(fixture.Subject,token);
            Assert.True(result.Outcome==EmailVerificationOutcome.Rejected && result.Grant is null);
            await transaction.CommitAsync();
        }
        fixture.Database.ChangeTracker.Clear();
        var user=await fixture.Database.Users.SingleAsync();
        Assert.True(!user.EmailConfirmed && user.Status==UserStatus.PendingVerification);
        Assert.True(await fixture.Database.Set<IdentityUserToken<Guid>>().CountAsync()==1);
    }

    [Fact]
    public async Task Replay_is_rejected_and_caller_rollback_restores_challenge_activation_and_grant_atomically()
    {
        await using var fixture=await Fixture.CreateAsync();
        var challenge=await fixture.PrepareAsync();
        await using(var transaction=await fixture.Database.Database.BeginTransactionAsync())
        {
            var result=await fixture.Exchange.VerifyAsync(fixture.Subject,challenge.GetTokenForDelivery());
            Assert.True(result.Outcome==EmailVerificationOutcome.Success);
            await transaction.RollbackAsync();
        }
        fixture.Database.ChangeTracker.Clear();
        Assert.True(!(await fixture.Database.Users.SingleAsync()).EmailConfirmed);
        Assert.True((await fixture.Database.Users.SingleAsync()).Status==UserStatus.PendingVerification);
        Assert.True(await fixture.Database.Set<IdentityUserToken<Guid>>().CountAsync()==1);
        await fixture.VerifyAsync(challenge);
        await using var retry=await fixture.Database.Database.BeginTransactionAsync();
        var replay=await fixture.Exchange.VerifyAsync(fixture.Subject,challenge.GetTokenForDelivery());
        Assert.True(replay.Outcome==EmailVerificationOutcome.Rejected && replay.Grant is null);
        await retry.CommitAsync();
        Assert.True(await fixture.Database.Set<IdentityUserToken<Guid>>().CountAsync()==1);
    }

    [Fact]
    public async Task Restricted_grant_requires_caller_transaction_exact_subject_purpose_and_one_use_with_rollback()
    {
        await using var fixture=await Fixture.CreateAsync();
        var grant=await fixture.VerifyAsync(await fixture.PrepareAsync());
        await Assert.ThrowsAsync<EmailVerificationExchangeException>(()=>fixture.Exchange.ConsumeRestrictedGrantAsync(
            grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
        await using(var transaction=await fixture.Database.Database.BeginTransactionAsync())
        {
            Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(Guid.NewGuid(),grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
            Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),"other-purpose"));
            Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,"wrong-credential",grant.Purpose));
            Assert.True(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
            Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
            await transaction.RollbackAsync();
        }
        fixture.Database.ChangeTracker.Clear();
        await using(var transaction=await fixture.Database.Database.BeginTransactionAsync())
        {
            Assert.True(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
            await transaction.CommitAsync();
        }
        await using var repeat=await fixture.Database.Database.BeginTransactionAsync();
        Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
        await repeat.CommitAsync();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("suspended")]
    [InlineData("unconfirmed")]
    public async Task Grant_rechecks_expiry_and_current_user_eligibility(string reason)
    {
        await using var fixture=await Fixture.CreateAsync();
        var grant=await fixture.VerifyAsync(await fixture.PrepareAsync());
        if(reason=="expired")fixture.Clock.Now=grant.ExpiresAtUtc;
        if(reason=="suspended")await fixture.Database.Users.Where(x=>x.Id==fixture.Subject).ExecuteUpdateAsync(x=>x.SetProperty(u=>u.Status,UserStatus.Suspended));
        if(reason=="unconfirmed")await fixture.Database.Users.Where(x=>x.Id==fixture.Subject).ExecuteUpdateAsync(x=>x.SetProperty(u=>u.EmailConfirmed,false));
        await using var transaction=await fixture.Database.Database.BeginTransactionAsync();
        Assert.False(await fixture.Exchange.ConsumeRestrictedGrantAsync(grant.SubjectId,grant.GrantId,grant.GetCredentialForExchange(),grant.Purpose));
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task Identity_update_refusal_rolls_back_prior_caller_work_and_clears_changed_identity_state()
    {
        await using var fixture=await Fixture.CreateAsync();
        var challenge=await fixture.PrepareAsync();
        await using(var transaction=await fixture.Database.Database.BeginTransactionAsync())
        {
            var earlier=User.Create("earlier@example.test","earlier","Earlier","Caller",fixture.Clock.Now);
            Assert.True((await fixture.Users.CreateAsync(earlier,Fixture.Password())).Succeeded);
            fixture.Users.UserValidators.Add(new RejectingValidator());
            await Assert.ThrowsAsync<EmailVerificationExchangeException>(()=>fixture.Exchange.VerifyAsync(fixture.Subject,challenge.GetTokenForDelivery()));
            Assert.False(fixture.Database.ChangeTracker.Entries().Any());
        }
        Assert.True(await fixture.Database.Users.CountAsync()==1);
        var user=await fixture.Database.Users.SingleAsync();
        Assert.True(user.Status==UserStatus.PendingVerification && !user.EmailConfirmed);
        Assert.True(await fixture.Database.Set<IdentityUserToken<Guid>>().CountAsync()==1);
    }

    [Theory]
    [InlineData(UserStatus.PendingVerification,false)]
    [InlineData(UserStatus.Active,true)]
    [InlineData(UserStatus.Suspended,true)]
    public async Task Activation_is_guarded_against_unconfirmed_or_nonpending_users(UserStatus status,bool confirmed)
    {
        await using var fixture=await Fixture.CreateAsync();
        var user=await fixture.Database.Users.SingleAsync();
        fixture.Database.Entry(user).Property(x=>x.Status).CurrentValue=status;user.EmailConfirmed=confirmed;
        Assert.Throws<InvalidOperationException>(()=>user.ActivateAfterEmailVerification(fixture.Clock.Now));
    }

    [Fact]
    public async Task Preparation_requires_caller_transaction_and_configuration_has_no_lifetime_default()
    {
        await using var fixture=await Fixture.CreateAsync();
        await Assert.ThrowsAsync<EmailVerificationExchangeException>(()=>fixture.Exchange.PrepareAsync(fixture.Subject));
        Assert.Throws<EmailVerificationExchangeException>(()=>new EmailVerificationExchange(fixture.Database,fixture.Users,fixture.Clock,
            Options.Create(new EmailVerificationExchangeOptions()),fixture.EmailOptions));
        Assert.Throws<EmailVerificationExchangeException>(()=>new EmailVerificationExchange(fixture.Database,fixture.Users,fixture.Clock,
            Options.Create(new EmailVerificationExchangeOptions{RestrictedGrantLifetime=TimeSpan.FromDays(1)}),fixture.EmailOptions));
    }

    private sealed class RejectingValidator : IUserValidator<User>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<User> manager,User user)=>Task.FromResult(
            IdentityResult.Failed(new IdentityError{Code="SyntheticRefusal",Description="Synthetic identity update refusal."}));
    }
    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now=DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow()=>Now; }
    private sealed class Fixture(IdentityTestContext context,AsyncServiceScope scope,Clock clock) : IAsyncDisposable
    {
        public AuthDbContext Database { get; }=scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        public UserManager<User> Users { get; }=scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        public IOptions<EmailConfirmationTokenProviderOptions> EmailOptions { get; }=scope.ServiceProvider.GetRequiredService<IOptions<EmailConfirmationTokenProviderOptions>>();
        public Clock Clock { get; }=clock;
        public Guid Subject { get; private set; }
        public EmailVerificationExchange Exchange=>new(Database,Users,Clock,
            Options.Create(new EmailVerificationExchangeOptions{RestrictedGrantLifetime=TimeSpan.FromMinutes(5)}),EmailOptions);
        public static string Password()=>Guid.NewGuid().ToString("N")+"aA1!";
        public static async Task<Fixture> CreateAsync()
        {
            var clock=new Clock();var context=await IdentityTestContext.CreateAsync(clock.Now);
            var scope=context.Services.CreateAsyncScope();var fixture=new Fixture(context,scope,clock);
            var user=User.Create(Guid.NewGuid().ToString("N")+"@example.test",Guid.NewGuid().ToString("N"),"Test","User",clock.Now);
            Assert.True((await fixture.Users.CreateAsync(user,Password())).Succeeded);fixture.Subject=user.Id;return fixture;
        }
        public async Task<EmailVerificationChallenge> PrepareAsync()
        {
            await using var transaction=await Database.Database.BeginTransactionAsync();
            var result=await Exchange.PrepareAsync(Subject);await transaction.CommitAsync();return result;
        }
        public async Task<RestrictedOnboardingGrant> VerifyAsync(EmailVerificationChallenge challenge)
        {
            await using var transaction=await Database.Database.BeginTransactionAsync();
            var result=await Exchange.VerifyAsync(Subject,challenge.GetTokenForDelivery());
            Assert.True(result.Outcome==EmailVerificationOutcome.Success && result.Grant is not null);
            await transaction.CommitAsync();return result.Grant!;
        }
        public async ValueTask DisposeAsync(){await scope.DisposeAsync();await context.DisposeAsync();}
    }
}
