namespace Reliant.Worker;

public sealed class WorkerService(ILogger<WorkerService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reliant Worker Host starting");
        return Task.CompletedTask;
    }
}
