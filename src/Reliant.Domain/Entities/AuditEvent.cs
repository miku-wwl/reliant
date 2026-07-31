namespace Reliant.Domain.Entities;

public class AuditEvent
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string ChangedBy { get; set; } = string.Empty;
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string? Metadata { get; set; }
}
