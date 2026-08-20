using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string? CausationId { get; set; }
    public string? TraceParent { get; set; }
    public string? TraceState { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public int SendCount { get; set; }
    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;
    public int Version { get; set; }
}
