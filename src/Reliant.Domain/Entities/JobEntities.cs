using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public static class KnownJobDefinitions
{
    public static readonly Guid ContributionProcessingId =
        Guid.Parse("7346d035-7e28-4dc8-b7b7-a982242df4ae");

    public const string ContributionProcessingName =
        "Contribution Processing";

    public const string ContributionProcessingHandler =
        "ProcessingHandler";

    public const string ContributionProcessingQueue =
        "reliant-processing";
}

public class JobDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string HandlerName { get; set; } = string.Empty;
    public int MaxAttempts { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 30;
    public string RetryPolicy { get; set; } = "exponential";
}

public class JobRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid JobDefinitionId { get; set; }
    public string QueueUrl { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public long FencingToken { get; set; }
    public int Version { get; set; }

    public static JobRun ForContributionProcessing(
        OutboxMessage message)
    {
        return new JobRun
        {
            Id = message.Id,
            OrganizationId = message.OrganizationId,
            JobDefinitionId =
                KnownJobDefinitions.ContributionProcessingId,
            QueueUrl =
                KnownJobDefinitions.ContributionProcessingQueue,
            MessageId = message.Id.ToString(),
            Payload = message.Payload,
            Status = JobStatus.Pending,
            CreatedAt = message.OccurredAt,
            FencingToken = 0,
            Version = 0
        };
    }

    public int StartAttempt(DateTime startedAt)
    {
        if (Status is JobStatus.Succeeded or
            JobStatus.Failed or
            JobStatus.DeadLettered or
            JobStatus.Cancelled)
        {
            throw new InvalidOperationException(
                $"Cannot start terminal job {Id} in state {Status}");
        }

        Status = JobStatus.Running;
        StartedAt ??= startedAt;
        CompletedAt = null;
        AttemptCount++;
        Version++;
        return AttemptCount;
    }

    public void MarkPending()
    {
        if (Status is JobStatus.Succeeded or
            JobStatus.Failed or
            JobStatus.DeadLettered or
            JobStatus.Cancelled)
        {
            return;
        }

        Status = JobStatus.Pending;
        CompletedAt = null;
        Version++;
    }

    public void MarkSucceeded(DateTime completedAt)
    {
        if (Status is JobStatus.Succeeded or
            JobStatus.Failed or
            JobStatus.DeadLettered or
            JobStatus.Cancelled)
        {
            return;
        }

        Status = JobStatus.Succeeded;
        CompletedAt = completedAt;
        Version++;
    }

    public void MarkDeadLettered(DateTime completedAt)
    {
        if (Status is JobStatus.Succeeded or
            JobStatus.Cancelled)
        {
            return;
        }

        Status = JobStatus.DeadLettered;
        CompletedAt = completedAt;
        Version++;
    }
}

public class JobAttempt
{
    public Guid Id { get; set; }
    public Guid JobRunId { get; set; }
    public int AttemptNumber { get; set; }
    public Guid? LeaseId { get; set; }
    public long FencingToken { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public JobAttemptStatus Status { get; set; } = JobAttemptStatus.Running;
    public ErrorCategory? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }

    public void Complete(
        JobAttemptStatus status,
        DateTime completedAt,
        string? errorMessage = null,
        ErrorCategory? errorCategory = null)
    {
        if (Status != JobAttemptStatus.Running)
        {
            return;
        }

        Status = status;
        CompletedAt = completedAt;
        ErrorMessage = errorMessage;
        ErrorCategory = errorCategory;
    }
}

public class Lease
{
    public Guid Id { get; set; }
    public Guid JobRunId { get; set; }
    public long FencingToken { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public readonly record struct JobExecutionFence(
    Guid JobRunId,
    Guid LeaseId,
    long FencingToken);

public sealed class StaleJobOwnerException(
    JobExecutionFence fence)
    : InvalidOperationException(
        $"Job {fence.JobRunId} rejected stale owner " +
        $"lease {fence.LeaseId} with fencing token " +
        $"{fence.FencingToken}")
{
    public JobExecutionFence Fence { get; } = fence;
}

public class Checkpoint
{
    public Guid Id { get; set; }
    public Guid JobRunId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime SavedAt { get; set; } = DateTime.UtcNow;
}

public class DeadLetterRecord
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string OriginalMessageId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
    public ErrorCategory? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTime DeadLetteredAt { get; set; } = DateTime.UtcNow;
    public DeadLetterStatus Status { get; set; } = DeadLetterStatus.Pending;
    public DateTime? ReplayedAt { get; set; }
    public int ReplayCount { get; set; }
    public string? ReplayMessageId { get; set; }
    public string? ReplayRequestedBy { get; set; }
}
