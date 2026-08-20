using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MediatR;
using Reliant.Application.Abstractions;
using Reliant.Application.Contributions.Commands;
using Reliant.Application.Observability;
using Reliant.Infrastructure.Persistence;
using System.Diagnostics;

namespace Reliant.Worker.Handlers;

public class ReconciliationHandlerService(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<ReconciliationHandlerService> logger) : BackgroundService
{
    private const int BatchSize = 20;
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(
        configuration.GetValue<int?>("Worker:Reconciliation:IntervalMs") ?? 60000);

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
                    var started = Stopwatch.GetTimestamp();
                    var telemetryResult = "failure";
                    ReliantTelemetry.ChangeWorkerInflight(
                        "reconciliation",
                        1);
                    using var activity = ReliantTelemetry.StartActivity(
                        "reconciliation process");
                    activity?.SetTag(
                        "reliant.contribution_id",
                        contributionId);
                    try
                    {
                        logger.LogInformation("Reconciling contribution {ContributionId}", contributionId);
                        var result = await sender.Send(new ReconcileContributionCommand(contributionId), stoppingToken);

                        if (result.Resolved)
                        {
                            telemetryResult = "success";
                            activity?.SetStatus(
                                ActivityStatusCode.Ok);
                            logger.LogInformation("Reconciliation resolved for {ContributionId}: {Resolution}", contributionId, result.Resolution);
                        }
                        else
                        {
                            telemetryResult = "deferred";
                            logger.LogWarning("Reconciliation pending for {ContributionId}: {Resolution}", contributionId, result.Resolution);
                        }
                    }
                    catch (Exception ex)
                    {
                        activity?.SetStatus(
                            ActivityStatusCode.Error,
                            ex.GetType().Name);
                        logger.LogError(ex, "Reconciliation error for {ContributionId}", contributionId);
                    }
                    finally
                    {
                        ReliantTelemetry.ChangeWorkerInflight(
                            "reconciliation",
                            -1);
                        ReliantTelemetry.RecordWorkerRun(
                            "reconciliation",
                            telemetryResult,
                            Stopwatch.GetElapsedTime(started));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Reconciliation handler error");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException) { }
        }

        logger.LogInformation("Reconciliation Handler stopped");
    }
}
