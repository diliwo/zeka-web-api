namespace AuthManager.Application.Common.Auditing;

public sealed record AuditEntryRequest(
    Guid ActorUserId,
    Guid? OrganisationId,
    string Action,
    string SubjectType,
    string SubjectId,
    string Outcome,
    string CorrelationId,
    IReadOnlyDictionary<string, string>? Metadata = null);
