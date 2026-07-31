using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Worker.Scheduling;

namespace Reliant.Worker.Handlers;

public class ScheduledMaintenanceHandlerService(
    IServiceProvider serviceProvider,
    ILogger<ScheduledMaintenanceHandlerService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private const int IntervalSeconds = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduled Maintenance Handler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var leaseRepo = scope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                var retryScheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();

                var expiredLeases = await leaseRepo.GetExpiredAsync(stoppingToken);
                foreach (var lease in expiredLeases)
                {
                    await leaseRepo.ReleaseAsync(lease.Id, stoppingToken);
                    logger.LogWarning("Released expired lease {LeaseId} for worker {WorkerId}", lease.Id, lease.WorkerId);
                }

                await retryScheduler.DispatchDueRetriesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Maintenance handler error");
            }

            try
            {
                var delay = TimeSpan.FromSeconds(IntervalSeconds);
                await Task.Delay(delay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Scheduled Maintenance Handler stopped");
    }
}
