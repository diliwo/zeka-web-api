using AuthManager.Application.Common.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace AuthManager.Infrastructure.Outbox;

internal sealed class OutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxDispatcherOptions> options,
    ILogger<OutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                await dispatcher.DispatchBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "The outbox dispatch cycle failed; it will be retried after the poll interval.");
            }

            await Task.Delay(options.Value.PollInterval, stoppingToken);
        }
    }
}
