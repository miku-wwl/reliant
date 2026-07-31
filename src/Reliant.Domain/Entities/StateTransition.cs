using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class StateTransition
{
    public Guid Id { get; set; }
    public Guid ContributionId { get; set; }
    public ContributionState FromState { get; set; }
    public ContributionState ToState { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
