namespace AuthManager.Application.Common.Auditing;

public interface IAuditWriter
{
    Task<Guid> WriteAsync(AuditEntryRequest request, CancellationToken cancellationToken = default);
}
