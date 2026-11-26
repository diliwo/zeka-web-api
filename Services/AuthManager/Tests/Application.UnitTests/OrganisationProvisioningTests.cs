using System.Text.Json;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Organisations;

namespace Application.UnitTests;

// Controlled ports prove use-case orchestration only; they do not prove database/provider atomicity.
public sealed class OrganisationProvisioningTests
{
    [Fact]
    public async Task Owner_creation_uses_domain_owner_authority_and_records_no_agreements()
    {
        var ports=new Ports();var result=await ports.Service.ProvisionAsync(Command());
        Assert.True(result.Outcome==OrganisationProvisioningOutcome.Created);
        var org=Assert.Single(ports.Organisations);var member=Assert.Single(ports.Memberships);
        Assert.True(org.Id==result.OrganisationId && org.OwnerUserId==ports.Identity.SubjectId && org.Status==OrganisationStatus.Active);
        Assert.True(org.TimeZoneId is null && org.TimeZoneConfigVersion==0);
        Assert.True(member.Id==result.MembershipId && member.OrganisationId==org.Id && member.UserId==org.OwnerUserId &&
            member.PermissionSetId==PermissionSet.OrganisationOwnerId && member.Status==MembershipStatus.Active);
        Assert.True(TenantPermissions.Resolve(member.PermissionSetId,"Owner")!.Contains("Organisations.Close"));
        var audit=Assert.Single(ports.Audits);
        Assert.True(audit.Action=="organisation.provisioned" && audit.SubjectType=="Organisation" &&
            audit.Metadata!["agreementStatus"]=="operator-provisioned, no agreements");
        Assert.False(ports.Audits.Any(x=>x.Action=="agreement.accepted" || x.SubjectType=="AgreementAcceptance"));
        var message=Assert.Single(ports.Messages);
        using var payload=JsonDocument.Parse(message.Payload);
        Assert.True(payload.RootElement.EnumerateObject().Count()==3);
        Assert.True(payload.RootElement.EnumerateObject().All(x=>x.Value.TryGetGuid(out _)));
        Assert.True(message.MessageType==OrganisationProvisioningEvents.MessageType && message.ContractVersion==1 &&
            message.CorrelationId==audit.CorrelationId && message.OrganisationId==org.Id);
        Assert.True(ports.Confirms==1 && ports.Additions==1 && ports.Completions==1 && ports.Commits==1 && ports.Rollbacks==0);
    }

    [Fact]
    public async Task Replay_uses_same_safe_ids_and_canonical_intent_without_recreating_or_elevating_identity()
    {
        var ports=new Ports();var first=await ports.Service.ProvisionAsync(Command());
        ports.Now=ports.Now.AddDays(1);
        var replay=await ports.Service.ProvisionAsync(Command() with {Agreements=Array.Empty<AgreementConsent>()});
        Assert.True(replay.Outcome==OrganisationProvisioningOutcome.Replay && replay.SubjectId==first.SubjectId &&
            replay.OrganisationId==first.OrganisationId && replay.MembershipId==first.MembershipId);
        Assert.True(ports.Confirms==1 && ports.Additions==1 && ports.Completions==1 && ports.Executions==2);
        Assert.True(ports.Canonicals.Count==2 && ports.Canonicals[0]==ports.Canonicals[1]);
        Assert.False(ports.Canonicals[0].Contains(first.OrganisationId.ToString("D"),StringComparison.Ordinal));
        Assert.False(ports.Canonicals[0].Contains(first.MembershipId.ToString("D"),StringComparison.Ordinal));
        Assert.Single(ports.Audits);Assert.Single(ports.Messages);
    }

    [Theory]
    [InlineData(IdempotencyDecisionKind.Conflict,OrganisationProvisioningOutcome.Conflict)]
    [InlineData(IdempotencyDecisionKind.Processing,OrganisationProvisioningOutcome.Processing)]
    public async Task Conflicting_or_processing_claims_cannot_provision(IdempotencyDecisionKind decision,OrganisationProvisioningOutcome outcome)
    {
        var ports=new Ports{Decision=new(decision)};
        var result=await ports.Service.ProvisionAsync(Command());
        Assert.True(result.Outcome==outcome && result.OrganisationId==Guid.Empty && result.MembershipId==Guid.Empty);
        Assert.True(ports.Confirms==0 && ports.Additions==0 && ports.Completions==0);
        Assert.Empty(ports.Audits);Assert.Empty(ports.Messages);
    }

    [Fact]
    public async Task Owner_path_rejects_any_claimed_agreement_before_transaction()
    {
        var ports=new Ports();
        var error=await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>ports.Service.ProvisionAsync(Command() with{
            Agreements=new[]{new AgreementConsent("synthetic-fixture-document","synthetic-fixture-version")}}));
        Assert.True(error.Message=="PROVISIONING_OWNER_AGREEMENTS_REJECTED" && ports.Executions==0 && ports.Confirms==0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_subject_is_fail_closed_until_a_future_self_service_scope_is_implemented(bool includesAgreement)
    {
        var ports=new Ports();ports.Identity=ports.Identity with{AuthorityKind=ProvisioningAuthorityKind.VerifiedSubject};
        var request=Command() with{Agreements=includesAgreement?new[]{new AgreementConsent("fixture","fixture-version")}:null};
        var error=await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>ports.Service.ProvisionAsync(request));
        Assert.True(error.Message=="SELF_SERVICE_NOT_ENABLED" && ports.Executions==0 && ports.Confirms==0);
    }

    [Fact]
    public async Task Invalid_trusted_authority_and_changed_confirmed_subject_are_rejected()
    {
        var invalid=new Ports();invalid.Identity=invalid.Identity with{AuthorityKind=(ProvisioningAuthorityKind)999};
        await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>invalid.Service.ProvisionAsync(Command()));
        Assert.True(invalid.Executions==0);
        var changed=new Ports{ConfirmedSubjectOverride=Guid.NewGuid()};
        await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>changed.Service.ProvisionAsync(Command()));
        Assert.True(changed.Additions==0 && changed.Rollbacks==1 && changed.Commits==0);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("catalogue")]
    [InlineData("repository")]
    [InlineData("audit")]
    [InlineData("outbox")]
    [InlineData("completion")]
    public async Task Failure_reaches_the_single_transaction_boundary_without_retry_or_partial_success(string stage)
    {
        var ports=new Ports{FailAt=stage};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ports.Service.ProvisionAsync(Command()));
        Assert.True(ports.Executions==1 && ports.Rollbacks==1 && ports.Commits==0 && ports.Claims==1);
        Assert.Empty(ports.Organisations);Assert.Empty(ports.Memberships);Assert.Empty(ports.Audits);Assert.Empty(ports.Messages);
    }

    [Fact]
    public async Task Invalid_options_and_corrupt_replay_fail_closed_and_command_has_no_privilege_or_credential_flags()
    {
        var options=new Ports{Options=new OrganisationProvisioningOptions()};
        await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>options.Service.ProvisionAsync(Command()));
        Assert.True(options.Executions==0);
        var replay=new Ports{Decision=new(IdempotencyDecisionKind.Replay,Guid.NewGuid(),"{}")};
        await Assert.ThrowsAsync<OrganisationProvisioningException>(()=>replay.Service.ProvisionAsync(Command()));
        Assert.True(replay.Confirms==0 && replay.Rollbacks==1);
        var names=typeof(OrganisationProvisioningCommand).GetProperties().Select(x=>x.Name).ToArray();
        Assert.False(names.Any(x=>x.Contains("Password",StringComparison.OrdinalIgnoreCase) ||
            x.Contains("EmailConfirmed",StringComparison.OrdinalIgnoreCase) || x.Contains("Admin",StringComparison.OrdinalIgnoreCase)));
    }

    private static OrganisationProvisioningCommand Command()=>new("  Fixture organisation  "," fixture-slug ","fixture-operation",null);
    private sealed class Ports : TimeProvider,IProvisioningIdentitySource,IProvisioningTransaction,IOrganisationProvisioningRepository,
        IIdempotencyStore,IAuditWriter,IOutboxWriter
    {
        public ProvisioningIdentity Identity=new(Guid.NewGuid(),ProvisioningAuthorityKind.PrivilegedLocalOwner);
        public Guid? ConfirmedSubjectOverride;
        public DateTimeOffset Now=new(2026,10,30,12,0,0,TimeSpan.Zero);
        public OrganisationProvisioningOptions Options=new(){ProcessingTimeout=TimeSpan.FromMinutes(1),Retention=TimeSpan.FromDays(1)};
        public IdempotencyDecision Decision=new(IdempotencyDecisionKind.Started);
        public string? FailAt;
        public int Executions,Commits,Rollbacks,Claims,Confirms,Additions,Completions;
        public bool Active,Confirmed;
        public List<Organisation> Organisations=[];public List<OrganisationMembership> Memberships=[];
        public List<AuditEntryRequest> Audits=[];public List<OutboxMessageRequest> Messages=[];public List<string> Canonicals=[];
        public OrganisationProvisioningService Service=>new(this,this,this,this,this,this,this,Options);
        public override DateTimeOffset GetUtcNow()=>Now;
        public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken=default)=>Task.FromResult(Identity);
        public Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity,CancellationToken cancellationToken=default)
        { Assert.True(Active && Claims==1);Fail("identity");Confirms++;Confirmed=true;return Task.FromResult(ConfirmedSubjectOverride??identity.SubjectId); }
        public async Task<OrganisationProvisioningResult> ExecuteAsync(Func<CancellationToken,Task<OrganisationProvisioningResult>> operation,
            CancellationToken cancellationToken=default)
        {
            Assert.False(Active);Executions++;Active=true;
            try{var result=await operation(cancellationToken);Commits++;return result;}
            catch{Rollbacks++;Organisations.Clear();Memberships.Clear();Audits.Clear();Messages.Clear();throw;}
            finally{Active=false;}
        }
        public Task ValidateOwnerCatalogueAsync(CancellationToken cancellationToken=default)
        { Assert.True(Active && Confirmed);Fail("catalogue");return Task.CompletedTask; }
        public Task AddAsync(Organisation organisation,OrganisationMembership ownerMembership,CancellationToken cancellationToken=default)
        { Assert.True(Active && Confirmed);Fail("repository");Additions++;Organisations.Add(organisation);Memberships.Add(ownerMembership);return Task.CompletedTask; }
        public Task<IdempotencyDecision> TryBeginAsync(Guid userId,string operation,string key,string canonicalRequest,
            TimeSpan processingTimeout,TimeSpan retention,CancellationToken cancellationToken=default)
        { Assert.True(Active && userId==Identity.SubjectId && operation==OrganisationProvisioningService.Operation);Claims++;Canonicals.Add(canonicalRequest);return Task.FromResult(Decision); }
        public Task CompleteAsync(Guid userId,string operation,string key,Guid resourceId,string safeResponse,CancellationToken cancellationToken=default)
        { Assert.True(Active);Fail("completion");Completions++;Decision=new(IdempotencyDecisionKind.Replay,resourceId,safeResponse);return Task.CompletedTask; }
        public Task<Guid> WriteAsync(AuditEntryRequest request,CancellationToken cancellationToken=default)
        { Assert.True(Active && Confirmed);Fail("audit");Audits.Add(request);return Task.FromResult(Guid.NewGuid()); }
        public Task<Guid> EnqueueAsync(OutboxMessageRequest request,CancellationToken cancellationToken=default)
        { Assert.True(Active && Confirmed);Fail("outbox");Messages.Add(request);return Task.FromResult(Guid.NewGuid()); }
        private void Fail(string stage){if(FailAt==stage)throw new InvalidOperationException("Synthetic port refusal.");}
    }
}
