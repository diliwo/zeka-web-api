using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Authentication;
using AuthManager.Core.Enums;
using AuthManager.Infrastructure.CustomTokenProviders;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthManager.Infrastructure.Identity.EmailVerification;

public sealed class EmailVerificationExchangeOptions
{
    public const string SectionName = "AuthOnboarding";
    public TimeSpan RestrictedGrantLifetime { get; set; }
}

public sealed class EmailVerificationExchange : IEmailVerificationExchange
{
    private const string Provider = "Zeka.EmailVerification";
    private const string ChallengeName = "email-confirmation";
    private const string ChallengePurpose = "email-verification";
    private readonly AuthDbContext database;
    private readonly UserManager<User> users;
    private readonly TimeProvider clock;
    private readonly TimeSpan grantLifetime;
    private readonly TimeSpan emailLifetime;
    private sealed record State(string Purpose, Guid SubjectId, Guid Jti, DateTimeOffset IssuedAtUtc,
        DateTimeOffset ExpiresAtUtc, string Hash)
    { public override string ToString() => "Verification state (hash redacted)."; }

    public EmailVerificationExchange(AuthDbContext database, UserManager<User> users, TimeProvider clock,
        IOptions<EmailVerificationExchangeOptions> options, IOptions<EmailConfirmationTokenProviderOptions> emailOptions)
    {
        this.database=database; this.users=users; this.clock=clock;
        grantLifetime=options.Value.RestrictedGrantLifetime;
        emailLifetime=emailOptions.Value.TokenLifespan;
        if(grantLifetime != TimeSpan.FromMinutes(5) || emailLifetime <= TimeSpan.Zero)
            throw Safe("EMAIL_VERIFICATION_CONFIGURATION_REJECTED");
    }

    public async Task<EmailVerificationChallenge> PrepareAsync(Guid subjectId, CancellationToken cancellationToken=default)
    {
        RequireTransaction();
        try
        {
            var user=await users.FindByIdAsync(subjectId.ToString("D"));
            if(user is null || user.Status != UserStatus.PendingVerification || user.EmailConfirmed)
                throw Safe("EMAIL_VERIFICATION_PREPARATION_REJECTED");
            if(await Tokens().AnyAsync(x=>x.UserId==subjectId && x.LoginProvider==Provider && x.Name==ChallengeName,cancellationToken))
                throw Safe("EMAIL_VERIFICATION_CHALLENGE_ALREADY_EXISTS");
            var token=await users.GenerateEmailConfirmationTokenAsync(user);
            var now=clock.GetUtcNow();
            var state=new State(ChallengePurpose,subjectId,Guid.NewGuid(),now,now+emailLifetime,Hash(ChallengePurpose,token));
            Tokens().Add(new(){UserId=subjectId,LoginProvider=Provider,Name=ChallengeName,Value=JsonSerializer.Serialize(state)});
            await database.SaveChangesAsync(cancellationToken);
            return new(subjectId,state.Jti,state.ExpiresAtUtc,token);
        }
        catch(OperationCanceledException){await RollbackAsync();throw;}
        catch(EmailVerificationExchangeException){await RollbackAsync();throw;}
        catch{await RollbackAsync();throw Safe("EMAIL_VERIFICATION_PREPARATION_UNAVAILABLE");}
    }

    public async Task<EmailVerificationExchangeResult> VerifyAsync(Guid subjectId,string token,CancellationToken cancellationToken=default)
    {
        RequireTransaction();
        if(subjectId==Guid.Empty || string.IsNullOrWhiteSpace(token))return Rejected();
        try
        {
            var user=await LockUserAsync(subjectId,cancellationToken);
            if(user is null || user.Status!=UserStatus.PendingVerification || user.EmailConfirmed)return Rejected();
            var row=await ReadAsync(subjectId,ChallengeName,cancellationToken);
            var now=clock.GetUtcNow();
            if(!Valid(row,subjectId,ChallengePurpose,token,now,out _))return Rejected();
            if(!await users.VerifyUserTokenAsync(user,users.Options.Tokens.EmailConfirmationTokenProvider,
                UserManager<User>.ConfirmEmailTokenPurpose,token))return Rejected();
            if(await DeleteExactAsync(row!,cancellationToken)!=1)return Rejected();
            var confirmation=await users.ConfirmEmailAsync(user,token);
            if(!confirmation.Succeeded)throw Safe("EMAIL_VERIFICATION_IDENTITY_UPDATE_REJECTED");
            user.ActivateAfterEmailVerification(now);
            if(!(await users.UpdateAsync(user)).Succeeded)throw Safe("EMAIL_VERIFICATION_IDENTITY_UPDATE_REJECTED");
            var credential=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
            var state=new State(OnboardingGrantPurposes.CreateOrganisation,subjectId,Guid.NewGuid(),now,now+grantLifetime,
                Hash(OnboardingGrantPurposes.CreateOrganisation,credential));
            Tokens().Add(new(){UserId=subjectId,LoginProvider=Provider,Name=GrantName(state.Jti),Value=JsonSerializer.Serialize(state)});
            await database.SaveChangesAsync(cancellationToken);
            return new(EmailVerificationOutcome.Success,new(subjectId,state.Jti,state.IssuedAtUtc,state.ExpiresAtUtc,credential));
        }
        catch(OperationCanceledException){await RollbackAsync();throw;}
        catch(EmailVerificationExchangeException){await RollbackAsync();throw;}
        catch{await RollbackAsync();throw Safe("EMAIL_VERIFICATION_EXCHANGE_UNAVAILABLE");}
    }

    public async Task<bool> ConsumeRestrictedGrantAsync(Guid subjectId,Guid grantId,string credential,string purpose,
        CancellationToken cancellationToken=default)
    {
        RequireTransaction();
        if(subjectId==Guid.Empty || grantId==Guid.Empty || purpose!=OnboardingGrantPurposes.CreateOrganisation ||
            string.IsNullOrWhiteSpace(credential))return false;
        try
        {
            var user=await LockUserAsync(subjectId,cancellationToken);
            if(user is null || user.Status!=UserStatus.Active || !user.EmailConfirmed)return false;
            var row=await ReadAsync(subjectId,GrantName(grantId),cancellationToken);
            return Valid(row,subjectId,purpose,credential,clock.GetUtcNow(),out var state) && state!.Jti==grantId &&
                await DeleteExactAsync(row!,cancellationToken)==1;
        }
        catch(OperationCanceledException){await RollbackAsync();throw;}
        catch(EmailVerificationExchangeException){await RollbackAsync();throw;}
        catch{await RollbackAsync();throw Safe("ONBOARDING_GRANT_EXCHANGE_UNAVAILABLE");}
    }

    private DbSet<IdentityUserToken<Guid>> Tokens()=>database.Set<IdentityUserToken<Guid>>();
    private Task<IdentityUserToken<Guid>?> ReadAsync(Guid subjectId,string name,CancellationToken ct)=>Tokens().AsNoTracking()
        .SingleOrDefaultAsync(x=>x.UserId==subjectId && x.LoginProvider==Provider && x.Name==name,ct);
    private async Task<User?> LockUserAsync(Guid subjectId,CancellationToken ct)
    {
        var tracked=database.ChangeTracker.Entries<User>().SingleOrDefault(x=>x.Entity.Id==subjectId);
        if(tracked is not null)
        {
            if(tracked.State!=EntityState.Unchanged)throw Safe("EMAIL_VERIFICATION_TRACKED_USER_WRITE_REJECTED");
            tracked.State=EntityState.Detached;
        }
        if(database.Database.ProviderName=="Npgsql.EntityFrameworkCore.PostgreSQL")
            return await database.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\"={subjectId} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
        if(database.Database.ProviderName=="Microsoft.EntityFrameworkCore.Sqlite")
            return await database.Users.SingleOrDefaultAsync(x=>x.Id==subjectId,ct);
        throw Safe("EMAIL_VERIFICATION_PROVIDER_REJECTED");
    }
    private async Task<int> DeleteExactAsync(IdentityUserToken<Guid> row,CancellationToken ct)
    {
        foreach(var entry in database.ChangeTracker.Entries<IdentityUserToken<Guid>>().Where(x=>x.Entity.UserId==row.UserId &&
            x.Entity.LoginProvider==row.LoginProvider && x.Entity.Name==row.Name).ToArray())
        {
            if(entry.State!=EntityState.Unchanged)throw Safe("EMAIL_VERIFICATION_TRACKED_TOKEN_WRITE_REJECTED");
            entry.State=EntityState.Detached;
        }
        return await Tokens().Where(x=>x.UserId==row.UserId && x.LoginProvider==row.LoginProvider && x.Name==row.Name && x.Value==row.Value)
            .ExecuteDeleteAsync(ct);
    }
    private static bool Valid(IdentityUserToken<Guid>? row,Guid subject,string purpose,string secret,DateTimeOffset now,out State? state)
    {
        state=null;
        try
        {
            if(row?.Value is null)return false;
            state=JsonSerializer.Deserialize<State>(row.Value);
            return state is not null && state.SubjectId==subject && state.Jti!=Guid.Empty && state.Purpose==purpose &&
                state.IssuedAtUtc<=now && state.ExpiresAtUtc>now && state.ExpiresAtUtc>state.IssuedAtUtc &&
                !string.IsNullOrEmpty(state.Hash) && state.Hash.Length==64 && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(state.Hash),
                    Convert.FromHexString(Hash(purpose,secret)));
        }
        catch(JsonException){return false;}
        catch(FormatException){return false;}
    }
    private static string Hash(string purpose,string secret)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose+"\n"+secret)));
    private static string GrantName(Guid id)=>"grant:"+id.ToString("N");
    private void RequireTransaction()
    {
        if(database.Database.CurrentTransaction is null)throw Safe("EMAIL_VERIFICATION_CALLER_TRANSACTION_REQUIRED");
    }
    private async Task RollbackAsync()
    {
        try{if(database.Database.CurrentTransaction is{}transaction)await transaction.RollbackAsync(CancellationToken.None);}
        catch{throw Safe("EMAIL_VERIFICATION_ROLLBACK_UNCERTAIN");}
        finally{database.ChangeTracker.Clear();}
    }
    private static EmailVerificationExchangeResult Rejected()=>new(EmailVerificationOutcome.Rejected,null);
    private static EmailVerificationExchangeException Safe(string code)=>new(code);
}
