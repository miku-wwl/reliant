using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Worker.Scheduling;

namespace Reliant.Worker.Handlers;

public class ScheduledMaintenanceHandlerService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ScheduledMaintenanceHandlerService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(
        configuration.GetValue<int?>("Worker:Maintenance:IntervalMs") ?? 30000);

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
                await Task.Delay(_interval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Scheduled Maintenance Handler stopped");
    }
}
