using Microsoft.Extensions.Logging;
using Reliant.Application.Abstractions;
using Reliant.Application.Dto;
using Reliant.Domain.Entities;
using Reliant.Domain.Enums;
using System.Text.Json;

namespace Reliant.Worker.Scheduling;

public interface IRetryScheduler
{
    Task<int> DispatchDueRetriesAsync(CancellationToken ct = default);
}

/// <summary>
/// Dispatches due retries. Designed to be safe under multiple concurrent
/// scheduler instances: due rows are claimed inside a transaction with
/// <c>FOR UPDATE SKIP LOCKED</c>, so a contribution can be dispatched only once.
/// </summary>
public class RetrySchedulerService : IRetryScheduler
{
    private const int BatchSize = 20;
    private const int MaxRetryAttempts = 5;

    private readonly IContributionRepository _contributionRepo;
    private readonly IStateTransitionRepository _stateTransitionRepo;
    private readonly IDeadLetterRepository _deadLetterRepo;
    private readonly IOutboxRepository _outboxRepo;
    private readonly IJobRunRepository _jobRunRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RetrySchedulerService> _logger;

    public RetrySchedulerService(
        IContributionRepository contributionRepo,
        IStateTransitionRepository stateTransitionRepo,
        IDeadLetterRepository deadLetterRepo,
        IOutboxRepository outboxRepo,
        IJobRunRepository jobRunRepo,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider,
        ILogger<RetrySchedulerService> logger)
    {
        _contributionRepo = contributionRepo;
        _stateTransitionRepo = stateTransitionRepo;
        _deadLetterRepo = deadLetterRepo;
        _outboxRepo = outboxRepo;
        _jobRunRepo = jobRunRepo;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<int> DispatchDueRetriesAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;

        await _unitOfWork.BeginTransactionAsync(ct);
        try
        {
            var retryDue = await _contributionRepo.GetRetryDueAsync(BatchSize, now, ct);
            var dispatched = 0;

            foreach (var contribution in retryDue)
            {
                // Atomic claim: only one concurrent scheduler wins the row.
                var claimed = await _contributionRepo.ClaimRetryDueAsync(contribution.Id, now, ct);
                if (claimed == 0)
                {
                    // Another scheduler already claimed and dispatched this one.
                    continue;
                }

                if (contribution.RetryCount >= MaxRetryAttempts)
                {
                    await MoveToDeadLetterAsync(contribution, ct);
                }
                else
                {
                    // Keep RetryPending; NextRetryAt is claimed (null) which marks it
                    // scheduled so the worker owns RetryPending -> Processing. Sync the
                    // in-memory entity so tracked reads are consistent with the DB.
                    contribution.NextRetryAt = null;
                    var retryMessage = new OutboxMessage
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = contribution.OrganizationId,
                        MessageType = "ContributionRetryRequested",
                        Payload = JsonSerializer.Serialize(new ContributionProcessingMessage(
                            Version: 1,
                            ContributionId: contribution.Id,
                            OrganizationId: contribution.OrganizationId,
                            Trigger: "Retry",
                            CorrelationId: Guid.NewGuid().ToString())),
                        CorrelationId = Guid.NewGuid().ToString(),
                        OccurredAt = now,
                        Status = OutboxStatus.Pending,
                        Version = 0
                    };
                    await _outboxRepo.AddAsync(retryMessage, ct);
                    await _jobRunRepo.AddAsync(
                        JobRun.ForContributionProcessing(retryMessage),
                        ct);

                    _logger.LogInformation("Contribution {ContributionId} scheduled for retry (retry #{RetryCount})", contribution.Id, contribution.RetryCount);
                }

                dispatched++;
            }

            await _unitOfWork.SaveChangesAsync(ct);
            await _unitOfWork.CommitAsync(ct);

            if (dispatched > 0)
            {
                _logger.LogInformation("Retry scheduler dispatched {Count} due retries", dispatched);
            }

            return dispatched;
        }
        catch
        {
            await _unitOfWork.RollbackAsync(ct);
            throw;
        }
    }

    private async Task MoveToDeadLetterAsync(Contribution contribution, CancellationToken ct)
    {
        var fromState = contribution.State;
        contribution.TransitionTo(ContributionState.Failed, "Max retry attempts exceeded");
        contribution.NextRetryAt = null;
        await _contributionRepo.UpdateAsync(contribution, ct);

        await _stateTransitionRepo.AddAsync(new StateTransition
        {
            Id = Guid.NewGuid(),
            ContributionId = contribution.Id,
            FromState = fromState,
            ToState = ContributionState.Failed,
            Reason = $"Max retry attempts ({MaxRetryAttempts}) exceeded",
            ChangedBy = "RetryScheduler"
        }, ct);

        await _deadLetterRepo.AddAsync(new DeadLetterRecord
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
        }, ct);

        await _outboxRepo.AddAsync(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OrganizationId = contribution.OrganizationId,
            MessageType = "OperatorAlert",
            Payload = JsonSerializer.Serialize(new
            {
                alert = "Contribution moved to dead letter after exhausting retries",
                contributionId = contribution.Id,
                retryCount = contribution.RetryCount
            }),
            CorrelationId = Guid.NewGuid().ToString(),
            OccurredAt = _timeProvider.GetUtcNow().UtcDateTime,
            Status = OutboxStatus.Pending,
            Version = 0
        }, ct);

        _logger.LogWarning("Contribution {ContributionId} moved to DLQ after max retries", contribution.Id);
    }
}
