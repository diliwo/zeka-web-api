namespace AuthManager.Infrastructure.Persistence.Entities;

public sealed class AuditEntry
{
    private AuditEntry() { }

    public Guid Id { get; private set; }
    public Guid ActorUserId { get; private set; }
    public Guid? OrganisationId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string SubjectType { get; private set; } = string.Empty;
    public string SubjectId { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string Metadata { get; private set; } = "{}";

    public static AuditEntry Create(Guid actorUserId, Guid? organisationId, string action,
        string subjectType, string subjectId, string outcome, DateTimeOffset occurredAtUtc,
        string correlationId, string metadata) => new()
    {
        Id = Guid.NewGuid(),
        ActorUserId = actorUserId,
        OrganisationId = organisationId,
        Action = action,
        SubjectType = subjectType,
        SubjectId = subjectId,
        Outcome = outcome,
        OccurredAtUtc = occurredAtUtc,
        CorrelationId = correlationId,
        Metadata = metadata
    };
}
