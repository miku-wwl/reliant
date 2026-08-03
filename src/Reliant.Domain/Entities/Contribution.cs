using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class Contribution
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid CampaignId { get; set; }
    public string ExternalReference { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public ContributionState State { get; set; } = ContributionState.Created;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int Version { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public ErrorCategory? LastErrorCategory { get; set; }
    public string? LastErrorMessage { get; set; }

    public bool CanTransitionTo(ContributionState target)
    {
        return ContributionStateMachine.IsValidTransition(State, target);
    }

    public void TransitionTo(ContributionState target, string reason)
    {
        if (!ContributionStateMachine.IsValidTransition(State, target))
        {
            throw new InvalidStateTransitionException(State, target);
        }

        State = target;
        UpdatedAt = DateTime.UtcNow;
        Version++;
    }
}
