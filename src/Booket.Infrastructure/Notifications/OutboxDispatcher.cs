using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Booket.Infrastructure.Notifications;

/// <summary>Background service that polls the outbox and delivers due notifications (confirmations, reminders...).</summary>
internal sealed class OutboxDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.DispatcherEnabled)
        {
            logger.LogInformation("Outbox dispatcher is disabled");
            return;
        }

        logger.LogInformation("Outbox dispatcher started (poll interval {PollInterval})", o.PollInterval);
        using var timer = new PeriodicTimer(o.PollInterval);

        do
        {
            try
            {
                int claimed;
                do
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
                    claimed = await processor.ProcessBatchAsync(stoppingToken);
                }
                while (claimed == o.BatchSize && !stoppingToken.IsCancellationRequested); // drain backlog
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
