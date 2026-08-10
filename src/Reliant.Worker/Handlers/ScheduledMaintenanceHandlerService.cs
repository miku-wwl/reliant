using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Domain.Enums;
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
    private readonly OperationalHistoryRetentionOptions _cleanupOptions =
        OperationalHistoryRetentionOptions.From(configuration);
    private DateTimeOffset _nextCleanupAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduled Maintenance Handler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var leaseRepo = scope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                var jobRunRepo = scope.ServiceProvider.GetRequiredService<IJobRunRepository>();
                var jobAttemptRepo = scope.ServiceProvider.GetRequiredService<IJobAttemptRepository>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var retryScheduler = scope.ServiceProvider.GetRequiredService<IRetryScheduler>();

                var scanStartedAt =
                    timeProvider.GetUtcNow().UtcDateTime;
                var expiredLeases = await leaseRepo.GetExpiredAsync(
                    scanStartedAt,
                    stoppingToken);
                foreach (var lease in expiredLeases)
                {
                    await unitOfWork.BeginTransactionAsync(stoppingToken);
                    try
                    {
                        var now = timeProvider.GetUtcNow().UtcDateTime;
                        var released = await leaseRepo.TryReleaseExpiredAsync(
                            lease.Id,
                            now,
                            stoppingToken);
                        if (!released)
                        {
                            await unitOfWork.RollbackAsync(stoppingToken);
                            continue;
                        }

                        var runningAttempt =
                            await jobAttemptRepo.GetRunningByJobRunAsync(
                                lease.JobRunId,
                                stoppingToken);
                        runningAttempt?.Complete(
                            JobAttemptStatus.Abandoned,
                            now,
                            $"Lease {lease.Id} expired while owned by {lease.WorkerId}");

                        var jobRun = await jobRunRepo.GetByIdAsync(
                            lease.JobRunId,
                            stoppingToken);
                        jobRun?.MarkPending();

                        await unitOfWork.SaveChangesAsync(stoppingToken);
                        await unitOfWork.CommitAsync(stoppingToken);

                        logger.LogWarning(
                            "Released expired lease {LeaseId} for worker {WorkerId}; job {JobRunId} returned to Pending",
                            lease.Id,
                            lease.WorkerId,
                            lease.JobRunId);
                    }
                    catch
                    {
                        await unitOfWork.RollbackAsync(
                            CancellationToken.None);
                        throw;
                    }
                }

                await retryScheduler.DispatchDueRetriesAsync(stoppingToken);

                if (_cleanupOptions.Enabled &&
                    timeProvider.GetUtcNow() >= _nextCleanupAt)
                {
                    _nextCleanupAt = timeProvider.GetUtcNow() +
                        _cleanupOptions.CleanupInterval;
                    var cleanup = scope.ServiceProvider
                        .GetRequiredService<
                            OperationalHistoryCleanupService>();
                    await cleanup.RunBatchAsync(stoppingToken);
                }
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
