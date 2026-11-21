using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthManager.Infrastructure.Onboarding;

internal sealed class VerificationMailWorker(IServiceScopeFactory scopes, ILogger<VerificationMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                for (var index = 0; index < 8; index++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(12));
                    if (!await scope.ServiceProvider.GetRequiredService<VerificationMailDispatcher>()
                        .DispatchOneAsync(deadline.Token))
                        break;
                }
            }
            catch (Exception) when (!stoppingToken.IsCancellationRequested)
            {
                // Provider and SMTP exception text can contain addresses or connection data.
                logger.LogWarning("Verification mail dispatch did not complete; the lease controls recovery.");
            }
        }
    }
}
