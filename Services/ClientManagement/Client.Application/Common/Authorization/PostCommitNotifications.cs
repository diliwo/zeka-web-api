using MediatR;

namespace ClientManagement.Application.Common.Authorization;

/// <summary>Defers in-process notifications until the owning tenant transaction has committed.</summary>
public sealed class PostCommitNotifications(IMediator? mediator = null)
{
    private readonly List<INotification> pending = [];

    public void Enqueue(INotification notification) => pending.Add(notification);
    public void Clear() => pending.Clear();

    public async Task PublishAsync(CancellationToken cancellationToken)
    {
        var committed = pending.ToArray();
        pending.Clear();
        if (committed.Length > 0 && mediator is null)
            throw new InvalidOperationException("A post-commit notification publisher is required.");
        foreach (var notification in committed)
            await mediator!.Publish(notification, cancellationToken);
    }
}
