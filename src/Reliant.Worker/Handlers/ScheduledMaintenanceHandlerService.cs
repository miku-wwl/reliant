using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Worker.Handlers;

public class ScheduledMaintenanceHandlerService(
    IServiceProvider serviceProvider,
    ILogger<ScheduledMaintenanceHandlerService> logger) : BackgroundService
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
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var expiredLeases = await leaseRepo.GetExpiredAsync(stoppingToken);
                foreach (var lease in expiredLeases)
                {
                    await leaseRepo.ReleaseAsync(lease.Id, stoppingToken);
                    logger.LogWarning("Released expired lease {LeaseId} for worker {WorkerId}", lease.Id, lease.WorkerId);
                }

                if (expiredLeases.Count > 0)
                {
                    await unitOfWork.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Maintenance handler error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Scheduled Maintenance Handler stopped");
    }
}
