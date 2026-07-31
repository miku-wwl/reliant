using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Messaging;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using Reliant.Infrastructure.Persistence;

namespace Reliant.Worker.Handlers;

public class ScheduledMaintenanceHandlerService(
    IServiceProvider serviceProvider,
    ILogger<ScheduledMaintenanceHandlerService> logger) : BackgroundService
{
    private const int IntervalSeconds = 30;
    private const int RetryBatchSize = 20;
    private const int MaxRetryAttempts = 5;
    private static readonly RetryPolicy RetryPolicy = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Scheduled Maintenance Handler started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var leaseRepo = scope.ServiceProvider.GetRequiredService<ILeaseRepository>();
                var contributionRepo = scope.ServiceProvider.GetRequiredService<IContributionRepository>();
                var stateTransitionRepo = scope.ServiceProvider.GetRequiredService<IStateTransitionRepository>();
                var deadLetterRepo = scope.ServiceProvider.GetRequiredService<IDeadLetterRepository>();
                var outboxRepo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                var expiredLeases = await leaseRepo.GetExpiredAsync(stoppingToken);
                foreach (var lease in expiredLeases)
                {
                    await leaseRepo.ReleaseAsync(lease.Id, stoppingToken);
                    logger.LogWarning("Released expired lease {LeaseId} for worker {WorkerId}", lease.Id, lease.WorkerId);
                }

                var retryDue = await contributionRepo.GetRetryDueAsync(RetryBatchSize, stoppingToken);
                foreach (var contribution in retryDue)
                {
                    if (contribution.RetryCount >= MaxRetryAttempts)
                    {
                        var fromState = contribution.State;
                        contribution.TransitionTo(ContributionState.Failed, "Max retry attempts exceeded");
                        contribution.NextRetryAt = null;
                        await contributionRepo.UpdateAsync(contribution, stoppingToken);
                        await stateTransitionRepo.AddAsync(new StateTransition
                        {
                            Id = Guid.NewGuid(),
                            ContributionId = contribution.Id,
                            FromState = fromState,
                            ToState = ContributionState.Failed,
                            Reason = $"Max retry attempts ({MaxRetryAttempts}) exceeded",
                            ChangedBy = "Scheduler"
                        }, stoppingToken);
                        await deadLetterRepo.AddAsync(new DeadLetterRecord
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = contribution.OrganizationId,
                            OriginalMessageId = contribution.Id.ToString(),
                            MessageType = "ContributionRetryExhausted",
                            Payload = $"Contribution {contribution.Id} failed after {MaxRetryAttempts} attempts",
                            ErrorCategory = contribution.LastErrorCategory,
                            ErrorMessage = contribution.LastErrorMessage,
                            AttemptCount = contribution.RetryCount,
                            Status = DeadLetterStatus.Pending
                        }, stoppingToken);
                        logger.LogWarning("Contribution {ContributionId} moved to DLQ after max retries", contribution.Id);
                    }
                    else
                    {
                        var fromState = contribution.State;
                        contribution.TransitionTo(ContributionState.Processing, "Retry scheduled");
                        contribution.NextRetryAt = null;
                        await contributionRepo.UpdateAsync(contribution, stoppingToken);
                        await stateTransitionRepo.AddAsync(new StateTransition
                        {
                            Id = Guid.NewGuid(),
                            ContributionId = contribution.Id,
                            FromState = fromState,
                            ToState = ContributionState.Processing,
                            Reason = "Retry scheduled by maintenance handler",
                            ChangedBy = "Scheduler"
                        }, stoppingToken);
                        await outboxRepo.AddAsync(new OutboxMessage
                        {
                            Id = Guid.NewGuid(),
                            OrganizationId = contribution.OrganizationId,
                            MessageType = "ContributionRetryRequested",
                            Payload = System.Text.Json.JsonSerializer.Serialize(new { contributionId = contribution.Id, organizationId = contribution.OrganizationId }),
                            CorrelationId = Guid.NewGuid().ToString(),
                            OccurredAt = DateTime.UtcNow,
                            Status = OutboxStatus.Pending,
                            Version = 0
                        }, stoppingToken);
                        logger.LogInformation("Contribution {ContributionId} scheduled for retry", contribution.Id);
                    }
                }

                if (expiredLeases.Count > 0 || retryDue.Count > 0)
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
