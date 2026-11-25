using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Provisioning;
using AuthManager.Core.Organisations;

namespace Application.UnitTests;

// Port tests prove orchestration/order only. PostgreSQL tests own lock, savepoint and atomicity proof.
public sealed class BrowserOrganisationCreationTests
{
    [Fact]
    public async Task Verified_creation_locks_current_identity_before_claim_and_records_only_local_deferral()
    {
        var ports = new Ports();
        var result = await ports.Service.ProvisionAsync(Command());
        Assert.Equal(OrganisationProvisioningOutcome.Created, result.Outcome);
        Assert.Equal(new[] { "identity", "index", "claim", "count", "catalogue", "add", "audit", "complete" }, ports.Order);
        var organisation = Assert.Single(ports.Organisations);
        var owner = Assert.Single(ports.Memberships);
        Assert.Equal(ports.Subject, organisation.OwnerUserId);
        Assert.Equal(OrganisationStatus.Active, organisation.Status);
        Assert.Null(organisation.TimeZoneId);
        Assert.Equal(0, organisation.TimeZoneConfigVersion);
        Assert.Equal(organisation.Id, owner.OrganisationId);
        Assert.Equal(organisation.OwnerUserId, owner.UserId);
        Assert.Equal(PermissionSet.OrganisationOwnerId, owner.PermissionSetId);
        Assert.Equal(MembershipStatus.Active, owner.Status);
        Assert.Equal(OrganisationProvisioningAudit.LocalM1DeferredAgreements,
            Assert.Single(ports.Audits).Metadata!["agreementStatus"]);
        Assert.Empty(ports.Messages);
        Assert.True(ports.Completed && ports.Committed);
    }

    [Fact]
    public async Task Replay_rechecks_identity_but_precedes_cap_and_does_not_create_a_second_effect()
    {
        var ports = new Ports();
        var first = await ports.Service.ProvisionAsync(Command());
        ports.AlreadyOwned = 1;
        var replay = await ports.Service.ProvisionAsync(Command());
        Assert.Equal(OrganisationProvisioningOutcome.Replay, replay.Outcome);
        Assert.Equal(first.OrganisationId, replay.OrganisationId);
        Assert.Equal(first.MembershipId, replay.MembershipId);
        Assert.Equal(2, ports.IdentityChecks);
        Assert.Equal(1, ports.CountCalls);
        Assert.Single(ports.Organisations);
        Assert.Single(ports.Memberships);
        Assert.Single(ports.Audits);
        Assert.Empty(ports.Messages);
    }

    [Fact]
    public async Task Ownership_limit_refusal_rolls_back_the_claim_instead_of_committing_processing_state()
    {
        var ports = new Ports { AlreadyOwned = 1 };
        var failure = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => ports.Service.ProvisionAsync(Command()));
        Assert.Equal(BrowserOrganisationCreationCodes.LimitReached, failure.Code);
        Assert.Equal(1, ports.Rollbacks);
        Assert.False(ports.ClaimPresent || ports.Completed || ports.Committed);
        Assert.Empty(ports.Organisations);
        Assert.Empty(ports.Memberships);
        Assert.Empty(ports.Audits);
    }

    [Theory]
    [InlineData(IdempotencyDecisionKind.Conflict, OrganisationProvisioningOutcome.Conflict)]
    [InlineData(IdempotencyDecisionKind.Processing, OrganisationProvisioningOutcome.Processing)]
    public async Task Existing_claim_decisions_do_not_create_or_count_ownership(IdempotencyDecisionKind decision,
        OrganisationProvisioningOutcome expected)
    {
        var ports = new Ports { Decision = new(decision) };
        var result = await ports.Service.ProvisionAsync(Command());
        Assert.Equal(expected, result.Outcome);
        Assert.Equal(Guid.Empty, result.OrganisationId);
        Assert.Equal(Guid.Empty, result.MembershipId);
        Assert.Equal(1, ports.IdentityChecks);
        Assert.Equal(0, ports.CountCalls);
        Assert.Empty(ports.Organisations);
        Assert.Empty(ports.Audits);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("index")]
    [InlineData("add")]
    [InlineData("audit")]
    [InlineData("complete")]
    public async Task Failure_rolls_back_the_whole_verified_attempt(string stage)
    {
        var ports = new Ports { FailAt = stage };
        await Assert.ThrowsAsync<OrganisationProvisioningException>(() => ports.Service.ProvisionAsync(Command()));
        Assert.Equal(1, ports.Rollbacks);
        Assert.False(ports.ClaimPresent || ports.Completed || ports.Committed);
        Assert.Empty(ports.Organisations);
        Assert.Empty(ports.Memberships);
        Assert.Empty(ports.Audits);
        Assert.Empty(ports.Messages);
    }

    [Fact]
    public async Task Local_deferral_never_fabricates_supplied_agreement_acceptance()
    {
        var ports = new Ports();
        var failure = await Assert.ThrowsAsync<OrganisationProvisioningException>(() => ports.Service.ProvisionAsync(
            Command() with { Agreements = [new("synthetic-document", "synthetic-version")] }));
        Assert.Equal(BrowserOrganisationCreationCodes.InvalidRequest, failure.Code);
        Assert.False(ports.Active || ports.Committed || ports.ClaimPresent);
        Assert.Empty(ports.Order);
    }

    private static OrganisationProvisioningCommand Command() => new("Local synthetic organisation", null, "same-operation", null);

    private sealed class Ports : TimeProvider, IProvisioningIdentitySource, IProvisioningTransaction,
        IOrganisationProvisioningRepository, IOrganisationOwnershipPolicy, IIdempotencyStore, IAuditWriter, IOutboxWriter
    {
        public Guid Subject { get; } = Guid.NewGuid();
        public int AlreadyOwned, IdentityChecks, CountCalls, Rollbacks;
        public bool Active, ClaimPresent, Completed, Committed;
        public string? FailAt;
        public IdempotencyDecision Decision = new(IdempotencyDecisionKind.Started);
        public List<string> Order { get; } = [];
        public List<Organisation> Organisations { get; } = [];
        public List<OrganisationMembership> Memberships { get; } = [];
        public List<AuditEntryRequest> Audits { get; } = [];
        public List<OutboxMessageRequest> Messages { get; } = [];
        public OrganisationProvisioningService Service => new(this, this, this, this, this, this, this,
            new() { LocalM1SelfServiceEnabled = true, MaxOwnedOrganisations = 1,
                ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromDays(1) }, this);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1);
        public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProvisioningIdentity(Subject, ProvisioningAuthorityKind.VerifiedSubject));
        public Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity, CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("identity"); Fail("identity"); IdentityChecks++; return Task.FromResult(Subject); }
        public Task ValidateCreationCatalogueAsync(CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("index"); Fail("index"); return Task.CompletedTask; }
        public Task<int> CountOwnedAsync(Guid subjectId, CancellationToken cancellationToken = default)
        { Assert.True(Active && ClaimPresent); Assert.Equal(Subject, subjectId); Order.Add("count"); CountCalls++; return Task.FromResult(AlreadyOwned); }
        public async Task<OrganisationProvisioningResult> ExecuteAsync(Func<CancellationToken, Task<OrganisationProvisioningResult>> work,
            CancellationToken cancellationToken = default)
        {
            Assert.False(Active); Active = true;
            try { var result = await work(cancellationToken); Committed = true; return result; }
            catch { Rollbacks++; ClaimPresent = false; Completed = false; Organisations.Clear(); Memberships.Clear(); Audits.Clear(); throw; }
            finally { Active = false; }
        }
        public Task ValidateOwnerCatalogueAsync(CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("catalogue"); return Task.CompletedTask; }
        public Task AddAsync(Organisation organisation, OrganisationMembership ownerMembership, CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("add"); Fail("add"); Organisations.Add(organisation); Memberships.Add(ownerMembership); return Task.CompletedTask; }
        public Task<IdempotencyDecision> TryBeginAsync(Guid userId, string operation, string key, string canonicalRequest,
            TimeSpan processingTimeout, TimeSpan retention, CancellationToken cancellationToken = default)
        { Assert.True(Active && IdentityChecks > 0); Order.Add("claim"); Assert.Equal(Subject, userId); Assert.Contains(OrganisationProvisioningAudit.LocalM1DeferredAgreements, canonicalRequest);
          ClaimPresent = Decision.Kind == IdempotencyDecisionKind.Started; return Task.FromResult(Decision); }
        public Task CompleteAsync(Guid userId, string operation, string key, Guid resourceId, string safeResponse,
            CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("complete"); Fail("complete"); Completed = true; Decision = new(IdempotencyDecisionKind.Replay, resourceId, safeResponse); return Task.CompletedTask; }
        public Task<Guid> WriteAsync(AuditEntryRequest request, CancellationToken cancellationToken = default)
        { Assert.True(Active); Order.Add("audit"); Fail("audit"); Audits.Add(request); return Task.FromResult(Guid.NewGuid()); }
        public Task<Guid> EnqueueAsync(OutboxMessageRequest request, CancellationToken cancellationToken = default)
        { Messages.Add(request); return Task.FromResult(Guid.NewGuid()); }
        private void Fail(string stage)
        { if (FailAt == stage) throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.Unavailable); }
    }
}