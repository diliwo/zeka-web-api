namespace AuthManager.Infrastructure.Persistence.Entities;

public sealed class OutboxMessage
{
    private OutboxMessage() { }

    public Guid Id { get; private set; }
    public string MessageType { get; private set; } = string.Empty;
    public int ContractVersion { get; private set; }
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public Guid? OrganisationId { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset NextAttemptAtUtc { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; private set; }
    public string? LastError { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; private set; }

    public static OutboxMessage Create(string messageType, int contractVersion, string payload,
        DateTimeOffset occurredAtUtc, DateTimeOffset createdAtUtc, string correlationId,
        Guid? organisationId) => new()
    {
        Id = Guid.NewGuid(),
        MessageType = messageType,
        ContractVersion = contractVersion,
        Payload = payload,
        OccurredAtUtc = occurredAtUtc,
        CreatedAtUtc = createdAtUtc,
        CorrelationId = correlationId,
        OrganisationId = organisationId,
        NextAttemptAtUtc = createdAtUtc
    };

    public void AcquireLease(Guid leaseId, DateTimeOffset leaseExpiresAtUtc)
    {
        LeaseId = leaseId;
        LeaseExpiresAtUtc = leaseExpiresAtUtc;
    }

    public void MarkProcessed(DateTimeOffset now)
    {
        AttemptCount++;
        ProcessedAtUtc = now;
        LastError = null;
        LeaseId = null;
        LeaseExpiresAtUtc = null;
    }

    public void MarkFailed(DateTimeOffset now, DateTimeOffset nextAttemptAtUtc, string error, int maxAttempts)
    {
        AttemptCount++;
        LastError = error;
        LeaseId = null;
        LeaseExpiresAtUtc = null;

        if (AttemptCount >= maxAttempts)
            DeadLetteredAtUtc = now;
        else
            NextAttemptAtUtc = nextAttemptAtUtc;
    }
}
