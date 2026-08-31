namespace AuthManager.Infrastructure.Outbox;

public sealed class OutboxDispatcherOptions
{
    public const string SectionName = "Outbox";

    public bool Enabled { get; set; }
    public int BatchSize { get; set; } = 25;
    public int MaxAttempts { get; set; } = 8;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}
