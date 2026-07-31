using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class ProcessingAttempt
{
    public Guid Id { get; set; }
    public Guid ContributionId { get; set; }
    public Guid OrganizationId { get; set; }
    public int AttemptNumber { get; set; }
    public string ProviderName { get; set; } = "Sandbox";
    public string ProviderIdempotencyKey { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public AttemptStatus Status { get; set; } = AttemptStatus.Pending;
    public ErrorCategory? ErrorCategory { get; set; }
    public string? ErrorMessage { get; set; }
    public string RequestPayload { get; set; } = string.Empty;
    public string? ResponsePayload { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class ProviderReference
{
    public Guid Id { get; set; }
    public Guid ContributionId { get; set; }
    public Guid OrganizationId { get; set; }
    public string Reference { get; set; } = string.Empty;
    public string ProviderName { get; set; } = "Sandbox";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ReconciliationRecord
{
    public Guid Id { get; set; }
    public Guid ContributionId { get; set; }
    public Guid OrganizationId { get; set; }
    public ContributionState LocalState { get; set; }
    public string ProviderState { get; set; } = string.Empty;
    public ReconciliationDifference Difference { get; set; }
    public string Resolution { get; set; } = string.Empty;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
