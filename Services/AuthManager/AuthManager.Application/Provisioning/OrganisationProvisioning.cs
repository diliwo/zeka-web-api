using System.Text.Json;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Core.Organisations;

namespace AuthManager.Application.Provisioning;

// Input slot reserved for the future shared self-service path; C1 never records agreement acceptance.
public sealed record AgreementConsent(string DocumentId,string Version);
public sealed record OrganisationProvisioningCommand(string DisplayName,string? Slug,string IdempotencyKey,IReadOnlyList<AgreementConsent>? Agreements);
public enum ProvisioningAuthorityKind { PrivilegedLocalOwner, VerifiedSubject }
public sealed record ProvisioningIdentity(Guid SubjectId,ProvisioningAuthorityKind AuthorityKind);
public interface IProvisioningIdentitySource
{
    // Trusted adapter context, never a request-body confirmation/admin flag. Describe must not create identity.
    Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken=default);
    Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity,CancellationToken cancellationToken=default);
}
public enum OrganisationProvisioningOutcome { Created, Replay, Conflict, Processing }
public sealed record OrganisationProvisioningResult(OrganisationProvisioningOutcome Outcome,Guid SubjectId,Guid OrganisationId,Guid MembershipId);
public interface IProvisioningTransaction
{
    // Exactly one atomic attempt. All participating adapters share its Auth transaction; never replay an uncertain commit.
    Task<OrganisationProvisioningResult> ExecuteAsync(Func<CancellationToken,Task<OrganisationProvisioningResult>> operation,
        CancellationToken cancellationToken=default);
}
public interface IOrganisationProvisioningRepository
{
    Task ValidateOwnerCatalogueAsync(CancellationToken cancellationToken=default);
    Task AddAsync(Organisation organisation,OrganisationMembership ownerMembership,CancellationToken cancellationToken=default);
}
public sealed class OrganisationProvisioningOptions
{
    public TimeSpan ProcessingTimeout { get; init; }
    public TimeSpan Retention { get; init; }
}
public sealed class OrganisationProvisioningException(string code) : Exception(code) { }
public static class OrganisationProvisioningAudit
{
    public const string OperatorNoAgreements="operator-provisioned, no agreements";
}
public static class OrganisationProvisioningEvents
{
    public const string MessageType="organisation.provisioned";
    public const int ContractVersion=1;
}
public sealed record OrganisationProvisionedIdentifiers(Guid OrganisationId,Guid OwnerUserId,Guid OwnerMembershipId);

public sealed class OrganisationProvisioningService(
    IProvisioningIdentitySource identity,IProvisioningTransaction transaction,IOrganisationProvisioningRepository repository,
    IIdempotencyStore idempotency,IAuditWriter audit,IOutboxWriter outbox,TimeProvider clock,OrganisationProvisioningOptions options)
{
    public const string Operation="organisation.provisioning/v1";
    private const string ResultSchema="zeka.organisation-provisioning-result/v1";
    private sealed record SavedResult(string SchemaVersion,Guid SubjectId,Guid OrganisationId,Guid MembershipId);

    public async Task<OrganisationProvisioningResult> ProvisionAsync(OrganisationProvisioningCommand command,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if(options.ProcessingTimeout<=TimeSpan.Zero || options.Retention<=options.ProcessingTimeout)
            throw Error("PROVISIONING_IDEMPOTENCY_CONFIGURATION_REQUIRED");
        if(string.IsNullOrWhiteSpace(command.DisplayName) || command.DisplayName.Trim().Length>200 ||
            string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.Slug is not null &&
            (string.IsNullOrWhiteSpace(command.Slug) || command.Slug.Trim().Length>200))throw Error("PROVISIONING_REQUEST_REJECTED");
        var admitted=await identity.DescribeAsync(cancellationToken);
        if(admitted is null || admitted.SubjectId==Guid.Empty || !Enum.IsDefined(admitted.AuthorityKind))
            throw Error("PROVISIONING_AUTHORITY_REJECTED");
        if(admitted.AuthorityKind==ProvisioningAuthorityKind.VerifiedSubject)throw Error("SELF_SERVICE_NOT_ENABLED");
        if(command.Agreements is { Count:>0 })throw Error("PROVISIONING_OWNER_AGREEMENTS_REJECTED");
        // No credentials, generated identifiers, clocks or correlation identifiers enter canonical retry intent.
        // Null and empty agreement input are the same explicit owner-provisioned, no-agreements operation.
        var canonical=JsonSerializer.Serialize(new{schemaVersion=Operation,subjectId=admitted.SubjectId,
            authorityKind=admitted.AuthorityKind.ToString(),displayName=command.DisplayName.Trim(),slug=command.Slug?.Trim(),
            agreementStatus=OrganisationProvisioningAudit.OperatorNoAgreements});
        return await transaction.ExecuteAsync(async ct=>
        {
            var claim=await idempotency.TryBeginAsync(admitted.SubjectId,Operation,command.IdempotencyKey,canonical,
                options.ProcessingTimeout,options.Retention,ct);
            if(claim.Kind==IdempotencyDecisionKind.Replay)return Replay(claim,admitted.SubjectId);
            if(claim.Kind==IdempotencyDecisionKind.Conflict)return Empty(OrganisationProvisioningOutcome.Conflict,admitted.SubjectId);
            if(claim.Kind==IdempotencyDecisionKind.Processing)return Empty(OrganisationProvisioningOutcome.Processing,admitted.SubjectId);
            if(claim.Kind!=IdempotencyDecisionKind.Started)throw Error("PROVISIONING_IDEMPOTENCY_STATE_REJECTED");
            if(await identity.EnsureConfirmedAsync(admitted,ct)!=admitted.SubjectId)throw Error("PROVISIONING_IDENTITY_CHANGED");
            await repository.ValidateOwnerCatalogueAsync(ct);
            if(TenantPermissions.Resolve(PermissionSet.OrganisationOwnerId,"Owner") is not { Count:>0 })
                throw Error("PROVISIONING_OWNER_CATALOGUE_REJECTED");
            var now=clock.GetUtcNow();
            var organisation=Organisation.Create(Guid.NewGuid(),command.DisplayName,admitted.SubjectId,now,command.Slug);
            if(!organisation.Activate(now))throw Error("PROVISIONING_ORGANISATION_ACTIVATION_REJECTED");
            var membership=OrganisationMembership.CreateOwner(Guid.NewGuid(),organisation.Id,admitted.SubjectId,now);
            await repository.AddAsync(organisation,membership,ct);
            var correlationId=Guid.NewGuid().ToString("D");
            await audit.WriteAsync(new(admitted.SubjectId,organisation.Id,"organisation.provisioned","Organisation",
                organisation.Id.ToString("D"),"Created",correlationId,new Dictionary<string,string>{
                    ["authorityKind"]=admitted.AuthorityKind.ToString(),["ownerMembershipId"]=membership.Id.ToString("D"),
                    ["agreementStatus"]=OrganisationProvisioningAudit.OperatorNoAgreements}),ct);
            await outbox.EnqueueAsync(new(OrganisationProvisioningEvents.MessageType,OrganisationProvisioningEvents.ContractVersion,
                JsonSerializer.Serialize(new OrganisationProvisionedIdentifiers(organisation.Id,admitted.SubjectId,membership.Id)),
                now,correlationId,organisation.Id),ct);
            var saved=new SavedResult(ResultSchema,admitted.SubjectId,organisation.Id,membership.Id);
            await idempotency.CompleteAsync(admitted.SubjectId,Operation,command.IdempotencyKey,organisation.Id,JsonSerializer.Serialize(saved),ct);
            return new(OrganisationProvisioningOutcome.Created,admitted.SubjectId,organisation.Id,membership.Id);
        },cancellationToken);
    }
    private static OrganisationProvisioningResult Replay(IdempotencyDecision decision,Guid subject)
    {
        try
        {
            var saved=JsonSerializer.Deserialize<SavedResult>(decision.SafeResponse??"");
            if(saved is null || saved.SchemaVersion!=ResultSchema || saved.SubjectId!=subject || saved.OrganisationId==Guid.Empty ||
                saved.MembershipId==Guid.Empty || decision.ResourceId!=saved.OrganisationId)throw Error("PROVISIONING_REPLAY_REJECTED");
            return new(OrganisationProvisioningOutcome.Replay,saved.SubjectId,saved.OrganisationId,saved.MembershipId);
        }
        catch(JsonException){throw Error("PROVISIONING_REPLAY_REJECTED");}
    }
    private static OrganisationProvisioningResult Empty(OrganisationProvisioningOutcome outcome,Guid subject)=>new(outcome,subject,Guid.Empty,Guid.Empty);
    private static OrganisationProvisioningException Error(string code)=>new(code);
}
