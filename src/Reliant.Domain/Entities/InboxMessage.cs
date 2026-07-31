using Reliant.Domain.Enums;

namespace Reliant.Domain.Entities;

public class InboxMessage
{
    public Guid Id { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string HandlerName { get; set; } = string.Empty;
    public string HandlerVersion { get; set; } = string.Empty;
    public DateTime? ProcessedAt { get; set; }
    public int AttemptCount { get; set; }
    public InboxStatus Status { get; set; } = InboxStatus.Processing;
}
