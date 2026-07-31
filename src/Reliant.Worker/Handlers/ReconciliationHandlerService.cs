using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Worker.Handlers;

public class ReconciliationHandlerService(
    IServiceProvider serviceProvider,
    ILogger<ReconciliationHandlerService> logger) : BackgroundService
{
    private const int IntervalSeconds = 60;
    private const int BatchSize = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Reconciliation Handler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var reconciliationRepo = scope.ServiceProvider.GetRequiredService<IReconciliationRepository>();
                var sender = scope.ServiceProvider.GetRequiredService<ISender>();

                var pendingIds = await reconciliationRepo.GetReconciliationPendingContributionIdsAsync(BatchSize, stoppingToken);

                foreach (var contributionId in pendingIds)
                {
                    try
                    {
                        logger.LogInformation("Reconciling contribution {ContributionId}", contributionId);
                        var result = await sender.Send(new ReconcileContributionCommand(contributionId), stoppingToken);

                        if (result.Resolved)
                        {
                            logger.LogInformation("Reconciliation resolved for {ContributionId}: {Resolution}", contributionId, result.Resolution);
                        }
                        else
                        {
                            logger.LogWarning("Reconciliation pending for {ContributionId}: {Resolution}", contributionId, result.Resolution);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Reconciliation error for {ContributionId}", contributionId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation handler error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Reconciliation Handler stopped");
    }
}
