using System.Text.Json;
using AuthManager.Application.Common.Auditing;
using AuthManager.Infrastructure.Persistence.Entities;
using AuthManager.Infrastructure.Security;

namespace AuthManager.Infrastructure.Persistence.Services;

internal sealed class AuditWriter(AuthDbContext dbContext, TimeProvider timeProvider) : IAuditWriter
{
    public async Task<Guid> WriteAsync(AuditEntryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);
        SensitiveDataGuard.ValidateMetadata(request.Metadata, nameof(request));

        var metadata = JsonSerializer.Serialize(request.Metadata ?? new Dictionary<string, string>());
        var entry = AuditEntry.Create(request.ActorUserId, request.OrganisationId, request.Action,
            request.SubjectType, request.SubjectId, request.Outcome, timeProvider.GetUtcNow(),
            request.CorrelationId, metadata);
        dbContext.AuditEntries.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);
        return entry.Id;
    }
}
