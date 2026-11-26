using System.Data;
using AuthManager.Application.Common.Auditing;
using AuthManager.Application.Common.Idempotency;
using AuthManager.Application.Common.Outbox;
using AuthManager.Application.Provisioning;
using AuthManager.Infrastructure.Identity;
using AuthManager.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuthManager.Infrastructure.Provisioning;

/// <summary>The owner deferred agreements for isolated local M1 only, never for another environment.</summary>
public sealed class BrowserOrganisationCreationPolicy
{
    public const string SectionName = "LocalM1OrganisationCreation";
    public bool Enabled { get; }
    public int MaxOwnedOrganisations { get; }
    private BrowserOrganisationCreationPolicy(bool enabled, int maximum)
    { Enabled = enabled; MaxOwnedOrganisations = maximum; }

    public static BrowserOrganisationCreationPolicy Read(IConfiguration configuration, IHostEnvironment environment)
    {
        var supplied = configuration[SectionName + ":Enabled"];
        var enabled = false;
        if (supplied is not null && !bool.TryParse(supplied, out enabled)) throw Rejected();
        var rawMaximum = configuration[SectionName + ":MaxOwnedOrganisations"];
        var maximum = 1;
        if (rawMaximum is not null && !int.TryParse(rawMaximum, out maximum)) throw Rejected();
        if (maximum is < 1 or > 100 || enabled && !environment.IsEnvironment("LocalStack")) throw Rejected();
        return new(enabled, maximum);
    }
    private static InvalidOperationException Rejected() => new("LOCAL_ORGANISATION_CREATION_POLICY_REJECTED");
}

public sealed class BrowserOrganisationCreationService(AuthDbContext database, BrowserSessions sessions,
    IIdempotencyStore idempotency, IAuditWriter audit, IOutboxWriter outbox, TimeProvider clock,
    BrowserOrganisationCreationPolicy policy) : IBrowserOrganisationCreation
{
    public async Task<OrganisationProvisioningResult> CreateAsync(Guid subjectId, Guid sessionId,
        OrganisationProvisioningCommand command, CancellationToken cancellationToken = default)
    {
        if (!policy.Enabled) throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.Unavailable);
        if (subjectId == Guid.Empty || sessionId == Guid.Empty)
            throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.Unauthenticated);
        if (command is null || string.IsNullOrWhiteSpace(command.DisplayName) || command.DisplayName.Trim().Length > 200
            || string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Length > 200
            || command.Slug is not null || command.Agreements is { Count: > 0 })
            throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.InvalidRequest);
        var repository = new OrganisationProvisioningRepository(database);
        var service = new OrganisationProvisioningService(new CurrentBrowserIdentity(subjectId, sessionId, sessions),
            new OrganisationProvisioningTransaction(database, IsolationLevel.ReadCommitted), repository,
            idempotency, audit, outbox, clock, new OrganisationProvisioningOptions
            {
                LocalM1SelfServiceEnabled = true, MaxOwnedOrganisations = policy.MaxOwnedOrganisations,
                ProcessingTimeout = TimeSpan.FromMinutes(2), Retention = TimeSpan.FromDays(1)
            }, repository);
        try
        {
            return await service.ProvisionAsync(command, cancellationToken);
        }
        catch (OrganisationProvisioningPersistenceException exception)
        {
            throw new OrganisationProvisioningException(exception.RequiresInspection
                ? BrowserOrganisationCreationCodes.ResultUncertain
                : BrowserOrganisationCreationCodes.Unavailable);
        }
    }

    private sealed class CurrentBrowserIdentity(Guid subject, Guid session, BrowserSessions sessions) : IProvisioningIdentitySource
    {
        public Task<ProvisioningIdentity> DescribeAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProvisioningIdentity(subject, ProvisioningAuthorityKind.VerifiedSubject));
        public async Task<Guid> EnsureConfirmedAsync(ProvisioningIdentity identity, CancellationToken cancellationToken = default)
        {
            if (identity.SubjectId != subject || identity.AuthorityKind != ProvisioningAuthorityKind.VerifiedSubject
                || !await sessions.ValidateForProvisioningAsync(subject, session, cancellationToken))
                throw new OrganisationProvisioningException(BrowserOrganisationCreationCodes.Unauthenticated);
            return subject;
        }
    }
}
