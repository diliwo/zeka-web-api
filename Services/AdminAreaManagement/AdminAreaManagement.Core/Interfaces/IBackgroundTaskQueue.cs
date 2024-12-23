namespace AdminAreaManagement.Core.Interfaces;

public interface IBackgroundTaskQueue
{
    ValueTask QueueBackgroundWorkItemAsync(Func<CancellationToken, ValueTask> workItem);
    ValueTask<Func<CancellationToken, ValueTask>> DequeueAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<Func<CancellationToken, ValueTask>> ReadAllAsync(CancellationToken cancellationToken);
}