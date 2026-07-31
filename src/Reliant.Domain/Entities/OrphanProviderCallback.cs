namespace Reliant.Domain.Entities;

/// <summary>
/// A callback that arrived but could not be matched to any local Contribution
/// by ProviderReference or IdempotencyKey. Persisted for auditability instead
/// of being dropped.
/// </summary>
public sealed class OrphanProviderCallback
{
    public Guid Id { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string? ProviderReference { get; set; }
    public string? IdempotencyKey { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public bool Resolved { get; set; }
}
