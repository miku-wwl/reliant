using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

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
    public int Version { get; set; }
}

public class JobAttempt
{
    public Guid Id { get; set; }
    public Guid JobRunId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public bool Succeeded { get; set; }
    public ErrorCategory? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
}

public class Lease
{
    public Guid Id { get; set; }
    public Guid JobRunId { get; set; }
    public string WorkerId { get; set; } = string.Empty;
    public DateTime AcquiredAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public bool IsActive { get; set; } = true;
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
    public ErrorCategory? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
    public DateTime DeadLetteredAt { get; set; } = DateTime.UtcNow;
    public DeadLetterStatus Status { get; set; } = DeadLetterStatus.Pending;
    public DateTime? ReplayedAt { get; set; }
    public int ReplayCount { get; set; }
}
