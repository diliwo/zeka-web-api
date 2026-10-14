using System.Data;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthManager.Infrastructure.TimeZones;

public enum ConfigureTimeZoneOutcome { Committed, Denied, Uncertain }

/// <summary>
/// Disabled application core: no production route, owner key source, human trust source,
/// or privileged writer connection is registered. Do not call under runtime credentials.
/// </summary>
public sealed class ConfigureTimeZoneCore(
    AuthDbContext database, ConfigureTimeZoneApprovalVerifier verifier, TimeProvider clock)
{
    public async Task<ConfigureTimeZoneOutcome> ExecuteAsync(
        string ownerJws, ConfigureTimeZoneCommand command, HumanTimeZoneReleaseProof human,
        CancellationToken cancellationToken)
    {
        if (database.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Configuration writes require a non-retrying isolated connection.");
        var owner = await verifier.VerifyAsync(ownerJws, command);
        if (owner is null || !IsValidHumanRelease(human, owner, command.ArtifactSha256()))
            return ConfigureTimeZoneOutcome.Denied;
        if (database.ChangeTracker.Entries().Any(entry => entry.State is
                EntityState.Added or EntityState.Modified or EntityState.Deleted))
            return ConfigureTimeZoneOutcome.Denied;

        TimeZoneConfigurationAudit? attemptedReceipt = null;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            await TenantContextInitializer.InitializeAsync(database, transaction,
                command.OrganisationId, cancellationToken);
            var current = await database.Organisations.AsNoTracking()
                .Where(x => x.Id == command.OrganisationId && x.Status == OrganisationStatus.Active)
                .Select(x => new { x.TimeZoneId, x.TimeZoneConfigVersion })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null || current.TimeZoneConfigVersion != command.ExpectedConfigVersion
                || current.TimeZoneConfigVersion == long.MaxValue
                || current.TimeZoneId == command.TimeZoneId)
                return ConfigureTimeZoneOutcome.Denied;

            var affected = await database.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE public."Organisations"
                SET "TimeZoneId" = {command.TimeZoneId},
                    "TimeZoneConfigVersion" = "TimeZoneConfigVersion" + 1,
                    "ConcurrencyVersion" = "ConcurrencyVersion" + 1,
                    "UpdatedAtUtc" = {clock.GetUtcNow()}
                WHERE "Id" = {command.OrganisationId}
                    AND "Status" = {(int)OrganisationStatus.Active}
                    AND "TimeZoneConfigVersion" = {command.ExpectedConfigVersion}
                    AND "TimeZoneId" IS DISTINCT FROM {command.TimeZoneId}
                """, cancellationToken);
            if (affected != 1) return ConfigureTimeZoneOutcome.Denied;

            attemptedReceipt = TimeZoneConfigurationAudit.Create(
                command.ApprovalId, command.OrganisationId, command.ArtifactSha256(),
                GetKeyId(ownerJws), human.ExecutorSubject,
                human.ReviewerSubject, human.ReleaseEvidenceId, command.ChangeReference,
                current.TimeZoneId, command.TimeZoneId, current.TimeZoneConfigVersion,
                clock.GetUtcNow(), human.CorrelationId);
            database.TimeZoneConfigurationAudits.Add(attemptedReceipt);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ConfigureTimeZoneOutcome.Committed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DetachAttemptedReceipt();
            throw;
        }
        catch (Exception)
        {
            // A commit can be indeterminate. Never retry a signed approval automatically.
            DetachAttemptedReceipt();
            return ConfigureTimeZoneOutcome.Uncertain;
        }

        void DetachAttemptedReceipt()
        {
            if (attemptedReceipt is not null)
                database.Entry(attemptedReceipt).State = EntityState.Detached;
        }
    }

    public async Task<ConfigureTimeZoneOutcome> ReconcileAsync(
        ConfigureTimeZoneCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await TenantContextInitializer.InitializeAsync(database, transaction,
            command.OrganisationId, cancellationToken);
        var receipt = await database.TimeZoneConfigurationAudits.AsNoTracking()
            .Where(x => x.ApprovalId == command.ApprovalId)
            .Select(x => new { x.OrganisationId, x.ArtifactSha256, x.NewVersion })
            .SingleOrDefaultAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (receipt is null) return ConfigureTimeZoneOutcome.Uncertain;
        return receipt.OrganisationId == command.OrganisationId
            && receipt.ArtifactSha256 == command.ArtifactSha256()
            && command.ExpectedConfigVersion < long.MaxValue
            && receipt.NewVersion == command.ExpectedConfigVersion + 1
            ? ConfigureTimeZoneOutcome.Committed : ConfigureTimeZoneOutcome.Denied;
    }

    private static bool IsValidHumanRelease(
        HumanTimeZoneReleaseProof human, string owner, string expectedDigest) =>
        !string.IsNullOrWhiteSpace(human.ExecutorSubject)
        && !string.IsNullOrWhiteSpace(human.ReviewerSubject)
        && human.ExecutorSubject != human.ReviewerSubject
        && human.ExecutorSubject != owner && human.ReviewerSubject != owner
        && human.ExecutorSubject.Length <= 200 && human.ReviewerSubject.Length <= 200
        && !string.IsNullOrWhiteSpace(human.ReleaseEvidenceId)
        && human.ReleaseEvidenceId.Length <= 200
        && !string.IsNullOrWhiteSpace(human.CorrelationId)
        && human.CorrelationId.Length <= 200
        && human.ReviewedArtifactSha256 == expectedDigest;

    private static string GetKeyId(string compactJws) =>
        new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
            .ReadJsonWebToken(compactJws).Kid;
}
